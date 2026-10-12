import readline from 'node:readline';
import { appendFile } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';

// Owned scratch fixture, not proxy functionality. All effects are confined to ignored research.
const counter = resolve(process.argv[2] ?? '');
const research = resolve(import.meta.dirname, '..', '.research') + sep;
if (!counter.startsWith(research)) throw new Error('Counter must stay in the primary research directory');
const lines = readline.createInterface({ input: process.stdin });
for await (const line of lines) {
  if (!line.trim()) continue;
  const request = JSON.parse(line); if (request.id === undefined) continue;
  let result;
  if (request.method === 'initialize') { await appendFile(counter + '.initialized', 'initialized\n'); result = { protocolVersion: request.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'phase0-counter', version: '0.1.0' } }; }
  else if (request.method === 'tools/list') result = { tools: [{ name: 'count', description: 'Append one line to the controlled fixture counter.', inputSchema: { type: 'object', properties: {}, additionalProperties: false } }] };
  else if (request.method === 'tools/call' && request.params.name === 'count') {
    await appendFile(counter, 'executed\n');
    if (process.argv[3] && process.argv[4]) {
      await new Promise((done, reject) => {
        const control = spawn('dotnet', [process.argv[3], 'activate', process.argv[4]], { windowsHide: true, stdio: 'ignore' });
        control.on('error', reject); control.on('close', code => code === 0 ? done() : reject(new Error('Fixture control rejected')));
      });
    }
    const proof = 'phase0-mcp-ok:' + randomBytes(12).toString('hex');
    await appendFile(counter + '.proof', proof + '\n');
    result = { content: [{ type: 'text', text: proof }] };
  }
  else if (request.method === 'resources/list') result = { resources: [] };
  else if (request.method === 'resources/templates/list') result = { resourceTemplates: [] };
  else { process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32601, message: 'Unsupported fixture method' } }) + '\n'); continue; }
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, result }) + '\n');
}
