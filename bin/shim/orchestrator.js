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

// Waits before each retry of a failed provisioning: 5, 10, 20, 40 seconds, then 60 seconds 19 times,
// 23 waits for 24 tries and 1215 seconds of waiting in all (time spent inside a try is extra). It
// outlasts the minutes between a version bump landing on the default branch and the release being
// published, the window in which a first launch finds no engine archive for its version.
export const PROVISION_RETRY_DELAYS_MS = [5, 10, 20, 40, ...Array(19).fill(60)].map((s) => s * 1000);

export function runShim(opts) {
  const {
    stdin, stdout, catalog,
    serverName, serverVersion, protocolVersion,
    provision,            // async () => { ok, error?, retryable? }
    engineCommand, engineArgs,
    stderr = process.stderr,
    maxRestarts = 5,
    // Waits (ms) before each retry of a failed provisioning; its length bounds the tries.
    provisionRetryDelaysMs = PROVISION_RETRY_DELAYS_MS,
    now,                  // optional clock (ms) shared with the readiness state, for tests
  } = opts;

  const state = new ShimState({ now });
  const surface = new ServerSurface({ catalog, serverName, serverVersion, protocolVersion });
  let proxy = null;
  let restarts = 0;
  let stopped = false;      // set by stop(): nothing may start or be scheduled afterwards
  let retryTimer = null;

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

  // Provision, then start the engine. A failure that can clear up (the release for this version is
  // not published yet, a network fault) is retried by this shim after a growing wait, so a first
  // launch heals by itself instead of staying failed until the MCP host is restarted.
  //
  // `attempt` is the number of the try now running, and while the shim waits it is the number of the
  // try that just failed; the first try carries no attempt and no error.
  async function attempt(n, lastError) {
    if (stopped) return;
    const base = n === 1 ? {} : { attempt: n, error: lastError };
    state.set('provisioning', base);

    let live = true;       // a late progress tick of a finished try must not rewrite the state
    let r;
    try {
      r = await provision((p) => { if (live && !stopped) state.set('provisioning', { ...base, ...p }); });
    } catch (e) {
      r = { ok: false, error: e.message, retryable: true };
    }
    live = false;
    if (stopped) return;

    if (r && r.ok) { startEngine(); return; }

    const error = r ? r.error : 'unknown';
    stderr.write(`[total-recall:shim] provisioning failed: ${error}\n`);
    // `retryable` is defined only on a failure; anything that is not exactly true is final.
    if (!r || r.retryable !== true) {
      state.set('provisioning-failed', { error, retryable: false });
      return;
    }
    const delay = provisionRetryDelaysMs[n - 1];
    if (delay === undefined) {
      state.set('provisioning-failed', { error, retryable: false, gaveUp: true, attempts: n });
      stderr.write(`[total-recall:shim] provisioning gave up after ${n} tries\n`);
      return;
    }
    state.set('provisioning', { attempt: n, error, nextRetryAt: state.now() + delay });
    stderr.write(`[total-recall:shim] provisioning will be tried again in ${Math.round(delay / 1000)}s\n`);
    retryTimer = setTimeout(() => { retryTimer = null; run(n + 1, error); }, delay);
    retryTimer.unref?.();
  }

  // Nothing escapes `attempt` as a rejection: an unexpected throw (outside `provision`, which has
  // its own try) is a final failure, never a retry.
  function run(n, lastError) {
    attempt(n, lastError).catch((e) => {
      if (stopped) return;
      state.set('provisioning-failed', { error: e.message, retryable: false });
      stderr.write(`[total-recall:shim] provisioning error: ${e.message}\n`);
    });
  }
  run(1);

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
    stopped = true;
    if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
    try { rl.close(); } catch { /* best-effort */ }
    if (proxy) proxy.stop();
  }

  return { stop, _state: state };
}
