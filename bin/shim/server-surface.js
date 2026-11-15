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
    // The harness's own name (initialize clientInfo.name), once it sends a usable one.
    this._clientName = undefined;
  }

  // Records the harness's client name from an initialize request. Anything that is not a
  // non-empty string leaves the previous value alone.
  _noteClientName(msg) {
    const name = msg.params?.clientInfo?.name;
    if (typeof name !== 'string') return;
    const trimmed = name.trim().slice(0, 128);
    if (trimmed) this._clientName = trimmed;
  }

  // A session_start call carrying the harness's name as `host`, or null when there is nothing
  // to add. The inbound message is never mutated.
  _withHost(msg) {
    if (!this._clientName) return null;
    const args = msg.params.arguments;
    if (args === undefined || args === null) {
      return { ...msg, params: { ...msg.params, arguments: { host: this._clientName } } };
    }
    if (typeof args === 'object' && !Array.isArray(args)) {
      return { ...msg, params: { ...msg.params, arguments: { ...args, host: this._clientName } } };
    }
    return null;
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
        this._noteClientName(msg);
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
      case 'tools/call': {
        if (!state.ready) return { kind: 'respond', message: makeResult(msg.id, state.notReadyResult()) };
        if (msg.params?.name === 'session_start') {
          const message = this._withHost(msg);
          if (message) return { kind: 'proxy', message };
        }
        return { kind: 'proxy' };
      }
      default:
        return state.ready
          ? { kind: 'proxy' }
          : { kind: 'respond', message: makeError(msg.id, -32601, `Method not found: ${msg.method}`) };
    }
  }
}
