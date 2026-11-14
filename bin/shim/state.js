// bin/shim/state.js
//
// The shim's readiness state machine and the SINGLE place the not-ready
// contract is built. Phases:
//   init | provisioning | provisioning-failed | starting-engine |
//   proxying | engine-restarting | engine-failed
// Only `proxying` is "ready" (tool calls are forwarded to the live engine).

export class ShimState {
  // `now` is the clock (ms since the epoch); injectable so a test can move time.
  constructor({ now = Date.now } = {}) {
    this.phase = 'init';
    this.detail = {};
    this._now = now;
  }

  now() {
    return this._now();
  }

  set(phase, detail = {}) {
    this.phase = phase;
    this.detail = detail;
  }

  get ready() {
    return this.phase === 'proxying';
  }

  // Structured payload describing why the engine isn't serving yet. Mirrors the
  // engine's own model_not_ready shape so the using-total-recall skill's retry
  // loop handles it uniformly.
  //
  // `phase` is written after the detail: a progress tick carries its own `phase`
  // ('downloading', 'extracting') and must never replace the state's. A detail's
  // `nextRetryAt` (ms since the epoch) is turned into the seconds left at the
  // moment of the call, so the answer counts down instead of repeating the number
  // that was true when the wait began.
  notReadyPayload() {
    const { nextRetryAt, ...detail } = this.detail;
    const payload = { status: 'not_ready', ...detail, phase: this.phase };
    if (typeof nextRetryAt === 'number') {
      payload.retryInSeconds = Math.max(0, Math.ceil((nextRetryAt - this._now()) / 1000));
    }
    payload.hint = this._hint(payload);
    return payload;
  }

  // What a caller should do, from what the state knows. A failed provisioning that the shim
  // is retrying says so (and when); one the shim gave up on says it will not try again, so
  // that "retry in a moment" is never said about a state that does not change.
  _hint(payload) {
    if (this.phase === 'provisioning-failed') {
      return `total-recall could not set itself up (${payload.error ?? 'unknown error'}) and will not try again by itself. `
        + 'Restart the MCP host to try again.';
    }
    if (this.phase === 'provisioning' && payload.error) {
      if (payload.retryInSeconds !== undefined) {
        const when = payload.retryInSeconds > 0 ? `in about ${payload.retryInSeconds} s` : 'in a moment';
        return `The engine download failed (${payload.error}). total-recall will try again by itself `
          + `${when}; retry this tool call after that.`;
      }
      return `The engine download failed earlier (${payload.error}); total-recall is trying again now. `
        + 'Retry this tool call in a few seconds.';
    }
    return 'total-recall is still starting up; retry in a moment.';
  }

  // MCP tools/call RESULT (not a protocol error): isError:true + a text block
  // carrying the JSON payload. Every harness renders this to the model as text.
  notReadyResult() {
    return {
      content: [{ type: 'text', text: JSON.stringify(this.notReadyPayload()) }],
      isError: true,
    };
  }
}
