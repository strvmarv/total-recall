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
    stdin, stdout, catalog,
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
    const thisProxy = new EngineProxy({ command: engineCommand, args: engineArgs, stderr });
    proxy = thisProxy;

    // Exit handling must be idempotent PER INSTANCE and bound to THIS instance.
    // Two reasons:
    //  - When the engine exits *during* the handshake, EngineProxy both rejects
    //    the handshake promise (-> .catch below) AND calls onExit — without this
    //    guard onEngineExit would fire twice and schedule two restarts.
    //  - A late exit from an OLD instance must not read the NEW instance's state;
    //    capturing `thisProxy` avoids the stale module-level `proxy` race.
    let exitHandled = false;
    const handleExit = () => {
      if (exitHandled) return;
      exitHandled = true;
      onEngineExit(thisProxy);
    };

    thisProxy.onForward = toHarness;
    thisProxy.onExit = handleExit;
    thisProxy.startAndHandshake().then(() => {
      state.set('proxying');
      // A clean (re)start that reached proxying is not a boot-loop: reset the
      // restart budget so an engine that recovers isn't permanently killed by
      // crashes spread across a long-lived session.
      restarts = 0;
      // Authoritative, mode-specific tools/list is now available — nudge the
      // harness to re-fetch (best-effort; some harnesses ignore it).
      toHarness({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' });
    }).catch((e) => {
      stderr.write(`[total-recall:shim] engine handshake failed: ${e.message}\n`);
      handleExit();
    });
  }

  function onEngineExit(instance) {
    // Stop forwarding any late-buffered responses from this dead instance: a
    // response that raced in just before exit must not double up with the
    // synthesized not_ready below (two responses for one id confuse harnesses).
    instance.onForward = () => {};
    // Synthesize not_ready for any in-flight harness ids so nothing hangs.
    for (const id of instance.pendingIds()) toHarness(makeResult(id, state.notReadyResult()));

    if (restarts >= maxRestarts) {
      state.set('engine-failed', { lastStderr: instance.stderrTail() });
      stderr.write('[total-recall:shim] engine failed repeatedly; staying up in degraded mode\n');
      return;
    }
    restarts += 1;
    // Exponential backoff: 250ms, 500, 1000, 2000, 4000… capped at 8s (the cap
    // only bites if maxRestarts is raised beyond the default 5).
    const backoffMs = Math.min(8000, 250 * 2 ** (restarts - 1));
    state.set('engine-restarting', { attempt: restarts });
    const t = setTimeout(startEngine, backoffMs);
    t.unref?.();
  }

  // Provision, then start the engine.
  state.set('provisioning');
  Promise.resolve()
    .then(() => provision((p) => state.set('provisioning', p)))
    .then((r) => {
      if (r && r.ok) { startEngine(); return; }
      // `retryable` is recorded into the not_ready payload for the agent/skill to
      // see; this release does NOT auto-retry provisioning (recovery is model-
      // driven — the agent re-calls the tool). TODO: if automatic provisioning
      // retry is ever added, READ this flag back from state.detail here rather
      // than re-deriving it.
      state.set('provisioning-failed', { error: r ? r.error : 'unknown', retryable: r ? r.retryable : false });
      stderr.write(`[total-recall:shim] provisioning failed: ${r ? r.error : 'unknown'}\n`);
    })
    .catch((e) => {
      state.set('provisioning-failed', { error: e.message, retryable: true });
      stderr.write(`[total-recall:shim] provisioning error: ${e.message}\n`);
    });

  // Harness inbound loop. If the harness closes stdin (it disconnected), tear
  // down so we don't leak a zombie shim + orphaned engine child.
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
  }, () => stop());

  function stop() {
    try { rl.close(); } catch { /* best-effort */ }
    if (proxy) proxy.stop();
  }

  return { stop, _state: state };
}
