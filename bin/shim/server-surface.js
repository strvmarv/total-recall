// bin/shim/server-surface.js
//
// Routes each inbound HARNESS message to one of:
//   { kind: 'respond', message }  — the shim answers it directly
//   { kind: 'proxy' }             — forward to the engine (only when ready)
//   { kind: 'drop' }              — consume silently (notifications pre-ready)
//   { kind: 'shutdown', message } — answer, then begin shutdown
//
// The shim always owns initialize/ping/shutdown (instant, engine-independent).
// tools/list and tools/call are answered locally until the engine is proxying.

import { makeResult, makeError } from './jsonrpc-stdio.js';

export class ServerSurface {
  constructor({ catalog, serverName, serverVersion, protocolVersion }) {
    this._catalog = catalog;
    this._name = serverName;
    this._version = serverVersion;
    this._protocol = protocolVersion;
  }

  route(msg, state) {
    const isNotification = msg.id === undefined || msg.id === null;

    if (isNotification) {
      // The harness's notifications/initialized is for the shim; the engine
      // receives its OWN initialized from the proxy handshake. Other
      // notifications forward once proxying, otherwise drop.
      if (msg.method === 'notifications/initialized') return { kind: 'drop' };
      return state.ready ? { kind: 'proxy' } : { kind: 'drop' };
    }

    switch (msg.method) {
      case 'initialize':
        return {
          kind: 'respond',
          message: makeResult(msg.id, {
            protocolVersion: this._protocol,
            serverInfo: { name: this._name, version: this._version },
            capabilities: { tools: { listChanged: true } },
          }),
        };
      case 'ping':
        return { kind: 'respond', message: makeResult(msg.id, {}) };
      case 'shutdown':
        return { kind: 'shutdown', message: makeResult(msg.id, {}) };
      case 'tools/list':
        return state.ready
          ? { kind: 'proxy' }
          : { kind: 'respond', message: makeResult(msg.id, this._catalog) };
      case 'tools/call':
        return state.ready
          ? { kind: 'proxy' }
          : { kind: 'respond', message: makeResult(msg.id, state.notReadyResult()) };
      default:
        return state.ready
          ? { kind: 'proxy' }
          : { kind: 'respond', message: makeError(msg.id, -32601, `Method not found: ${msg.method}`) };
    }
  }
}
