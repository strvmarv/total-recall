// bin/shim/engine-proxy.js
//
// Owns the engine child process. Responsibilities:
//   - spawn the binary with piped stdio
//   - perform the shim->engine MCP handshake (initialize + notifications/initialized)
//     using a reserved id so it never collides with harness ids
//   - forward every other engine->shim message to the harness via onForward
//   - track pending harness request ids so a crash can synthesize responses
//   - tee engine stderr to a bounded ring buffer + pass through to our stderr
//   - report unexpected exit via onExit

import { spawn } from 'node:child_process';
import { readMessages, writeMessage } from './jsonrpc-stdio.js';

const HANDSHAKE_ID = '__shim_init__'; // string id; harness uses numbers/uuids
const STDERR_TAIL_MAX = 8192;         // chars of recent engine stderr to retain

export class EngineProxy {
  constructor({ command, args = [], stderr, handshakeTimeoutMs = 30000 }) {
    this._command = command;
    this._args = args;
    this._stderr = stderr ?? process.stderr;
    this._handshakeTimeoutMs = handshakeTimeoutMs;
    this._child = null;
    this._handshakeResolve = null;
    this._handshakeReject = null;
    this._pending = new Set();      // harness request ids forwarded, awaiting response
    this._stderrTail = '';          // byte-bounded tail of recent engine stderr
    this._stopped = false;

    this.onForward = () => {};       // (msg) => void  — engine -> harness
    this.onExit = () => {};          // (code, signal) => void
  }

  startAndHandshake() {
    if (this._child) throw new Error('EngineProxy.startAndHandshake() called twice');
    return new Promise((resolve, reject) => {
      this._handshakeResolve = resolve;
      this._handshakeReject = reject;

      this._child = spawn(this._command, this._args, { stdio: ['pipe', 'pipe', 'pipe'] });

      // The engine can die mid-write (crash-after-initialize): writing to a
      // dead pipe emits EPIPE on child.stdin. On POSIX an unhandled stream
      // error here can crash the shim, so absorb it — the 'exit' handler owns
      // the real recovery path.
      this._child.stdin.on('error', () => { /* EPIPE on dead engine; best-effort */ });

      readMessages(this._child.stdout, (msg) => this._onEngineMessage(msg));

      this._child.stderr.on('data', (b) => {
        const text = b.toString();
        // Keep the last STDERR_TAIL_MAX characters. Bounding by characters (not
        // by chunk count) means a pathological engine spraying tiny chunks
        // can't shrink the retained diagnostic window.
        this._stderrTail = (this._stderrTail + text).slice(-STDERR_TAIL_MAX);
        this._stderr.write(text);
      });

      this._child.on('exit', (code, signal) => {
        if (this._handshakeReject) {
          const rej = this._handshakeReject;
          this._handshakeResolve = this._handshakeReject = null;
          rej(new Error(`engine exited during handshake (code=${code}, signal=${signal})`));
          return;
        }
        if (!this._stopped) this.onExit(code, signal);
      });

      this._child.on('error', (err) => {
        if (this._handshakeReject) {
          const rej = this._handshakeReject;
          this._handshakeResolve = this._handshakeReject = null;
          rej(err);
        }
      });

      const timer = setTimeout(() => {
        if (this._handshakeReject) {
          const rej = this._handshakeReject;
          this._handshakeResolve = this._handshakeReject = null;
          rej(new Error('engine handshake timed out'));
          this.stop();
        }
      }, this._handshakeTimeoutMs);
      timer.unref();

      // Kick the handshake.
      writeMessage(this._child.stdin, {
        jsonrpc: '2.0', id: HANDSHAKE_ID, method: 'initialize',
        params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'total-recall-shim', version: '1' } },
      });
    });
  }

  _onEngineMessage(msg) {
    if (msg.id === HANDSHAKE_ID) {
      // Engine accepted initialize -> send initialized, mark ready. Guard the
      // write: a crash-after-initialize engine may already have a dead stdin.
      if (this._child.stdin.writable) {
        writeMessage(this._child.stdin, { jsonrpc: '2.0', method: 'notifications/initialized' });
      }
      if (this._handshakeResolve) {
        const res = this._handshakeResolve;
        this._handshakeResolve = this._handshakeReject = null;
        res();
      }
      return;
    }
    // Any response clears its pending id.
    if (msg.id !== undefined && msg.id !== null) this._pending.delete(msg.id);
    this.onForward(msg);
  }

  // Forward a harness request/notification to the engine; track request ids.
  send(msg) {
    if (!this._child || !this._child.stdin.writable) return;
    if (msg.id !== undefined && msg.id !== null) this._pending.add(msg.id);
    writeMessage(this._child.stdin, msg);
  }

  // Ids forwarded but not yet answered (used to synthesize responses on crash).
  pendingIds() { return [...this._pending]; }

  stderrTail() { return this._stderrTail; }

  stop() {
    this._stopped = true;
    if (this._child && !this._child.killed) {
      try { this._child.kill(); } catch { /* best-effort */ }
    }
  }
}
