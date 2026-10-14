// Read-only official protocol acceptance: isolated profile, no thread/start or model call.
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';

const [cliPath, bridgePath] = process.argv.slice(2);
if (process.argv.length !== 4 || !cliPath || !bridgePath || resolve(cliPath).toLowerCase() === resolve(bridgePath).toLowerCase())
  throw new Error('Specify exactly one official native CLI and one distinct built Bridge executable path.');
const root = resolve('.research', `compact-bridge-probe-${randomUUID()}`);
await mkdir(root, { recursive: true });
await mkdir(join(root, 'official-profile'), { recursive: true });
const child = spawn(resolve(bridgePath), ['app-server', '--listen', 'stdio://'], {
  env: { ...process.env, CODEX_POOL_UPSTREAM_CLI: resolve(cliPath), CODEX_POOL_COMPACT_BRIDGE: '1',
    CODEX_HOME: join(root, 'official-profile'), CODEX_POOL_DATA_DIR: join(root, 'empty-pool') },
  stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
});
child.stderr.resume(); // Never persist stderr or raw protocol frames.
const replies = new Map();
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  try {
    const reply = JSON.parse(line);
    if (replies.has(reply.id)) { const waiter = replies.get(reply.id); replies.delete(reply.id); waiter(reply); }
  } catch { /* invalid output fails the bounded pending call */ }
});
function call(id, method, params) {
  return new Promise((done, fail) => {
    const timeout = setTimeout(() => { replies.delete(id); fail(new Error('protocol_timeout')); }, 15_000);
    replies.set(id, reply => { clearTimeout(timeout); reply.error ? fail(new Error('protocol_rejected')) : done(reply.result); });
    child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
  });
}
let passed = false;
try {
  await call(1, 'initialize', { clientInfo: { name: 'codex_pool_bridge_probe', version: '0.1.0' } });
  child.stdin.write(JSON.stringify({ method: 'initialized' }) + '\n');
  const loaded = await call(2, 'thread/loaded/list', {});
  if (!Array.isArray(loaded.data) || loaded.data.length !== 0) throw new Error('unexpected_loaded_threads');
  passed = true;
} finally {
  child.stdin.end();
  await Promise.race([new Promise(done => child.once('exit', done)), new Promise(done => setTimeout(done, 5000))]);
  if (child.exitCode === null) child.kill();
  if (child.exitCode !== 0) passed = false;
  lines.close();
  await writeFile(join(root, 'result.json'), JSON.stringify({ passed, scope: 'official protocol handshake only', modelCalls: 0, threadsCreated: 0 }));
}
if (!passed) throw new Error('bridge_process_exit_failed');
console.log('PASS official 0.162.1 initialize, empty loaded-thread list and clean exit through Bridge; no model calls');
