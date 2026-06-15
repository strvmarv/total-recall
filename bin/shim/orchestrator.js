// bin/shim/orchestrator.js
//
// Wires the shim modules into the readiness state machine over a pair of
// streams (process.stdin/stdout in production; PassThroughs in tests). All
// external effects (provisioning, engine spawn) are injected so the golden
// test runs offline.

import { readMessages, writeMessage, makeResult } from './jsonrpc-stdio.js';
import { ShimState } from './state.js';
import { ServerSurface } from './server-surface.js';
import { EngineProxy } from './engine-proxy.js';

export function runShim(opts) {
  const {
    stdin, stdout, catalog, version,
    serverName, serverVersion, protocolVersion,
    provision,            // async () => { ok, error?, retryable? }
    engineCommand, engineArgs,
    stderr = process.stderr,
    maxRestarts = 5,
  } = opts;

  const state = new ShimState();
  const surface = new ServerSurface({ catalog, serverName, serverVersion, protocolVersion });
  let proxy = null;
  let restarts = 0;

  const toHarness = (msg) => writeMessage(stdout, msg);

  function startEngine() {
    state.set('starting-engine');
    proxy = new EngineProxy({ command: engineCommand, args: engineArgs, stderr });
    proxy.onForward = toHarness;
    proxy.onExit = onEngineExit;
    proxy.startAndHandshake().then(() => {
      state.set('proxying');
      // Authoritative, mode-specific tools/list is now available — nudge the
      // harness to re-fetch (best-effort; some harnesses ignore it).
      toHarness({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' });
    }).catch((e) => {
      stderr.write(`[total-recall:shim] engine handshake failed: ${e.message}\n`);
      onEngineExit(null, null);
    });
  }

  function onEngineExit(code, signal) {
    // Synthesize not_ready for any in-flight harness ids so nothing hangs.
    const pending = proxy ? proxy.pendingIds() : [];
    for (const id of pending) toHarness(makeResult(id, state.notReadyResult()));

    if (restarts >= maxRestarts) {
      state.set('engine-failed', { lastStderr: proxy ? proxy.stderrTail() : '' });
      stderr.write('[total-recall:shim] engine failed repeatedly; staying up in degraded mode\n');
      return;
    }
    restarts += 1;
    const backoffMs = Math.min(8000, 250 * 2 ** (restarts - 1));
    state.set('engine-restarting', { attempt: restarts });
    const t = setTimeout(startEngine, backoffMs);
    if (t.unref) t.unref();
  }

  // Provision, then start the engine.
  state.set('provisioning');
  Promise.resolve()
    .then(() => provision((p) => state.set('provisioning', p)))
    .then((r) => {
      if (r && r.ok) { startEngine(); return; }
      state.set('provisioning-failed', { error: r ? r.error : 'unknown', retryable: r ? r.retryable : false });
      stderr.write(`[total-recall:shim] provisioning failed: ${r ? r.error : 'unknown'}\n`);
    })
    .catch((e) => {
      state.set('provisioning-failed', { error: e.message, retryable: true });
      stderr.write(`[total-recall:shim] provisioning error: ${e.message}\n`);
    });

  // Harness inbound loop.
  const rl = readMessages(stdin, (msg) => {
    const decision = surface.route(msg, state);
    switch (decision.kind) {
      case 'respond': toHarness(decision.message); break;
      case 'proxy': if (proxy) proxy.send(msg); break;
      case 'drop': break;
      case 'shutdown':
        toHarness(decision.message);
        stop();
        break;
    }
  });

  function stop() {
    try { rl.close(); } catch { /* best-effort */ }
    if (proxy) proxy.stop();
  }

  return { stop, _state: state };
}
