// bin/shim/state.js
//
// The shim's readiness state machine and the SINGLE place the not-ready
// contract is built. Phases:
//   init | provisioning | provisioning-failed | starting-engine |
//   proxying | engine-restarting | engine-failed
// Only `proxying` is "ready" (tool calls are forwarded to the live engine).

export class ShimState {
  constructor() {
    this.phase = 'init';
    this.detail = {};
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
  notReadyPayload() {
    return {
      status: 'not_ready',
      phase: this.phase,
      ...this.detail,
      hint: 'total-recall is still starting up; retry in a moment.',
    };
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
