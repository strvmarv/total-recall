#!/usr/bin/env node
// Test fixture: a minimal MCP engine over stdio. Behaviors via argv:
//   (default)      answer initialize, echo tools/call as a text result
//   --crash-after-initialize   exit(1) right after the initialize handshake
//   --hang-initialize          never answer initialize
//   --log-stderr               emit a marker line on stderr at startup
import readline from 'node:readline';

const mode = process.argv[2] ?? '';
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
const write = (o) => process.stdout.write(JSON.stringify(o) + '\n');

if (mode === '--log-stderr') process.stderr.write('engine-diag-line\n');

rl.on('line', (line) => {
  const s = line.trim();
  if (!s) return;
  let msg;
  try { msg = JSON.parse(s); } catch { return; }
  if (msg.id === undefined || msg.id === null) return; // notification

  if (msg.method === 'initialize') {
    if (mode === '--hang-initialize') return;
    write({ jsonrpc: '2.0', id: msg.id, result: {
      protocolVersion: '2024-11-05',
      serverInfo: { name: 'fake-engine', version: '0.0.0' },
      capabilities: { tools: {} },
    } });
    if (mode === '--crash-after-initialize') process.exit(1);
    return;
  }
  if (msg.method === 'tools/list') {
    write({ jsonrpc: '2.0', id: msg.id, result: { tools: [{ name: 'real_tool', description: 'r', inputSchema: { type: 'object' } }] } });
    return;
  }
  if (msg.method === 'tools/call') {
    write({ jsonrpc: '2.0', id: msg.id, result: { content: [{ type: 'text', text: 'engine-ok' }], isError: false } });
    return;
  }
  write({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'nope' } });
});
