import { spawn } from 'node:child_process';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import assert from 'node:assert/strict';

// Opt-in real-account acceptance, after the user completes both official OAuth authorizations.
const root = resolve(import.meta.dirname, '..');
const scratch = join(root, '.research', `live-${Date.now()}`); const home = join(scratch, 'codex-home');
const assembly = join(root, 'src/CodexPool.Daemon/bin/Debug/net10.0-windows/CodexPool.Daemon.dll');
await mkdir(home, { recursive: true });
await writeFile(join(home, 'config.toml'), 'approval_policy = "never"\nsandbox_mode = "read-only"\n[model_providers.codex_pool]\nname = "Codex Pool"\nrequest_max_retries = 0\nstream_max_retries = 0\n');
const environment = { ...process.env, CODEX_HOME: home };
function control(args) {
  return new Promise((done, reject) => {
    const process = spawn('dotnet', [assembly, ...args], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }); let stdout = '';
    process.stdout.on('data', x => stdout += x); process.on('error', reject);
    process.on('close', code => code === 0 ? done(stdout) : reject(new Error('Local control command rejected')));
  });
}
const initial = JSON.parse(await control(['status']));
assert.equal(initial.distinctUsers, 2, 'Two different users are authorized'); assert(initial.boundaryProbe, 'Live boundary test mode explicitly enabled');
const accountA = initial.accounts.find(x => x.name === 'Account A'); const accountB = initial.accounts.find(x => x.name === 'Account B');
assert(accountA && accountB);
await writeFile(join(home, 'config.toml'), `approval_policy = "never"
sandbox_mode = "read-only"
[model_providers.codex_pool]
name = "Codex Pool"
request_max_retries = 0
stream_max_retries = 0
[projects.${JSON.stringify(scratch.replaceAll('\\', '/'))}]
trust_level = "trusted"
[mcp_servers.phase0]
command = "node"
default_tools_approval_mode = "approve"
args = [${[join(root, 'scripts/fixture-mcp.mjs'), join(scratch, 'mcp-counter.txt'), assembly, accountB.id].map(x => JSON.stringify(x.replaceAll('\\', '/'))).join(', ')}]
`);
const summary = { model: 'gpt-6-luna', realAccounts: true, steps: [], evidenceDirectory: scratch };
let rpc;
function startRpc() {
  const child = spawn('dotnet', [assembly, 'codex', 'app-server', '--listen', 'stdio://'], { cwd: scratch, env: environment, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const pending = new Map(); const notices = []; let buffer = '', id = 0, stderr = '';
  child.stderr.on('data', x => stderr += x);
  child.stdout.on('data', x => {
    buffer += x;
    while (buffer.includes('\n')) {
      const end = buffer.indexOf('\n'); const line = buffer.slice(0, end); buffer = buffer.slice(end + 1); if (!line.trim()) continue;
      const message = JSON.parse(line);
      if (message.id !== undefined && !message.method) {
        const waiter = pending.get(message.id); if (waiter) { pending.delete(message.id); message.error ? waiter.reject(new Error('Official RPC rejected')) : waiter.resolve(message.result); }
      } else notices.push(message);
    }
  });
  return {
    child, notices,
    call(method, params) { return new Promise((resolveCall, rejectCall) => {
      const requestId = ++id; const timer = setTimeout(() => { pending.delete(requestId); rejectCall(new Error(`${method}: timeout`)); }, 60000);
      pending.set(requestId, { resolve: value => { clearTimeout(timer); resolveCall(value); }, reject: error => { clearTimeout(timer); rejectCall(error); } });
      child.stdin.write(JSON.stringify({ id: requestId, method, params }) + '\n');
    }); },
    async initialize() { await this.call('initialize', { clientInfo: { name: 'codex_pool_phase0', version: '0.1.0' } }); child.stdin.write(JSON.stringify({ method: 'initialized' }) + '\n'); },
    async wait(predicate, from) { for (let i = 0; i < 1200; i++) { const found = notices.slice(from).find(predicate); if (found) return found; if (child.exitCode !== null) throw new Error('Official client exited'); await new Promise(r => setTimeout(r, 100)); } throw new Error('Official notification timeout'); },
    async close(label) {
      if (child.exitCode === null) {
        if (process.platform === 'win32') await new Promise(done => { const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' }); killer.on('close', done); });
        else child.kill();
      }
      await writeFile(join(scratch, `${label}.notifications.json`), JSON.stringify(notices, null, 2)); await writeFile(join(scratch, `${label}.stderr.txt`), stderr);
    }
  };
}
async function turn(threadId, text, label, expected, effort) {
  const from = rpc.notices.length; await rpc.call('turn/start', { threadId, input: [{ type: 'text', text }], ...(effort ? { effort } : {}) });
  const completed = await rpc.wait(x => x.method === 'turn/completed', from);
  const messages = rpc.notices.slice(from).filter(x => x.method === 'item/completed' && x.params.item.type === 'agentMessage').map(x => x.params.item.text).join('\n');
  summary.steps.push({ label, status: completed.params.turn.status, expectedFactPresent: expected ? messages.includes(expected) : null });
  await writeFile(join(scratch, 'summary.json'), JSON.stringify(summary, null, 2));
  assert.equal(completed.params.turn.status, 'completed', label); if (expected) assert(messages.includes(expected), `${label}: retained fact`);
}
const marker = `POOL-${Date.now().toString(36)}`; const code = '742913';
try {
  await control(['activate', accountA.id]); rpc = startRpc(); await rpc.initialize();
  const started = await rpc.call('thread/start', { cwd: scratch, model: 'gpt-6-luna' }); const threadId = started.thread.id;
  await turn(threadId, `Remember two test facts: marker ${marker}, code ${code}. Reply exactly RECORDED. Do not use tools.`, 'A first inference', 'RECORDED');
  console.log('PASS Account A real inference');
  await control(['activate', accountB.id]);
  await turn(threadId, 'Return the marker and code from the preceding message as marker|code, with no other text. Do not use tools.', 'B same-thread continuation', `${marker}|${code}`);
  console.log('PASS A -> B same-thread continuation');
  let from = rpc.notices.length; await rpc.call('thread/compact/start', { threadId });
  await rpc.wait(x => x.method === 'thread/compacted' || x.method === 'item/completed' && x.params.item.type === 'contextCompaction', from);
  summary.steps.push({ label: 'B official compact', status: 'completed' }); console.log('PASS Account B official compact');
  await control(['activate', accountA.id]);
  await turn(threadId, 'Return the previously recorded marker and code as marker|code, with no other text. Do not use tools.', 'A after B compact', `${marker}|${code}`);
  console.log('PASS B -> A after compact');
  await rpc.close('before-resume'); rpc = startRpc(); await rpc.initialize(); await rpc.call('thread/resume', { threadId });
  await control(['activate', accountB.id]);
  await turn(threadId, 'Return the recorded marker and code as marker|code, with no other text. Do not use tools.', 'B resumed thread', `${marker}|${code}`);
  console.log('PASS Resume across account boundary');
  await control(['activate', accountA.id]);
  await turn(threadId, `Compute 23*47+1689 and record the result as a new fact. Retain marker ${marker}. Explain your calculation in one sentence. Do not use tools.`, 'A reasoning context', '2770', 'medium');
  await control(['activate', accountB.id]);
  await turn(threadId, 'Return the arithmetic result recorded in the preceding turn. Reply with the number only. Do not use tools.', 'B after A reasoning', '2770');
  console.log('PASS Reasoning continuation boundary (inspect encrypted-item metadata)');
  await control(['activate', accountA.id]);
  await turn(threadId, 'Invoke the phase0 MCP server count tool exactly once. Do not use shell. After the tool returns, reply exactly phase0-mcp-ok. Do not invoke the tool again.', 'A tool -> B result continuation', 'phase0-mcp-ok');
  assert.equal((await readFile(join(scratch, 'mcp-counter.txt'), 'utf8')).trim().split(/\r?\n/).length, 1, 'Real MCP side effect executes once');
  console.log('PASS MCP side effect and tool-result account boundary');
  const status = JSON.parse(await control(['status']));
  summary.requests = status.recentRequests; summary.twoAccountsStillReady = status.accounts.every(x => x.auth === 'ready');
  summary.scope = 'Small HTTP/SSE session and local compact only; not a universal encrypted-state or Desktop guarantee';
  await writeFile(join(scratch, 'summary.json'), JSON.stringify(summary, null, 2));
  console.log(JSON.stringify(summary, null, 2));
} catch (error) {
  summary.failure = error.message; summary.requests = JSON.parse(await control(['status'])).recentRequests;
  await writeFile(join(scratch, 'summary.json'), JSON.stringify(summary, null, 2));
  console.error(`Live gate not passed: ${error.message}\nEvidence: ${scratch}`); process.exitCode = 1;
} finally { if (rpc) await rpc.close('final'); await control(['activate', accountA.id]); }
