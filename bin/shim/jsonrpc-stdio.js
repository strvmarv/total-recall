// bin/shim/jsonrpc-stdio.js
//
// Newline-delimited JSON-RPC framing for the MCP stdio transport (one JSON
// object per line, UTF-8). Used for BOTH the harness channel and the engine
// child channel. Zero dependencies.

import readline from 'node:readline';

// Read messages off a readable stream, invoking onMessage(obj) per parsed line.
// Blank lines are skipped; unparseable lines are dropped with a stderr note
// (a trusted MCP peer never emits these, but we must not crash the shim).
// onClose() fires when the stream ends. Returns the readline interface.
export function readMessages(readable, onMessage, onClose) {
  const rl = readline.createInterface({ input: readable, crlfDelay: Infinity });
  rl.on('line', (line) => {
    const s = line.trim();
    if (!s) return;
    let msg;
    try { msg = JSON.parse(s); }
    catch { process.stderr.write('[total-recall:shim] dropping unparseable line\n'); return; }
    onMessage(msg);
  });
  if (onClose) rl.on('close', onClose);
  return rl;
}

export function writeMessage(writable, obj) {
  writable.write(JSON.stringify(obj) + '\n');
}

export function makeResult(id, result) {
  return { jsonrpc: '2.0', id, result };
}

export function makeError(id, code, message) {
  return { jsonrpc: '2.0', id, error: { code, message } };
}
