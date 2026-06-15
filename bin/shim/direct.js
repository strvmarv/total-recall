// bin/shim/direct.js
//
// Runs a NON-serve engine subcommand (ui, reindex-embeddings, config,
// dump-catalog, …) as a plain child process: provision the binary, spawn it with
// inherited stdio, and mirror its exit status.
//
// Why this exists: only `serve` speaks the MCP JSON-RPC protocol on stdio. Every
// other subcommand emits HUMAN-READABLE output on stdout (the web UI prints its
// listening URL; reindex prints progress; dump-catalog prints JSON for a human).
// Routing those through the MCP shim made the shim (a) parse their stdout as
// protocol — the "[total-recall:shim] dropping unparseable line" flood — and
// (b) wait forever for an `initialize` response that never comes, time the
// handshake out, kill the child, and restart it until it gave up in degraded
// mode (killing the web server: "it stopped working"). Bypassing the shim for
// these subcommands is the fix.

import { spawn as realSpawn } from 'node:child_process';

/**
 * True when these args mean "run the MCP server" — the only mode that speaks
 * JSON-RPC on stdio and therefore belongs behind the shim. A bare invocation
 * (no args) defaults to serve, matching the engine's own dispatch
 * (Program.cs: `args.Length == 0 || args[0] == "serve"`).
 */
export function isServeInvocation(args) {
  return args.length === 0 || args[0] === 'serve';
}

/**
 * Provision the engine, then spawn it directly with inherited stdio and mirror
 * its exit status. All effects are injectable so the suite runs offline.
 *
 * @param {object}   opts
 * @param {string[]} opts.args            - argv to pass to the engine (e.g. ['ui', '--port', '5577'])
 * @param {Function} opts.provision       - async () => { ok, binaryPath } | { ok:false, error }
 * @param {Function} [opts.spawn]         - child_process.spawn (injected in tests)
 * @param {Writable} [opts.stderr]        - diagnostics sink (default process.stderr)
 * @param {Function} [opts.onExit]        - (code, signal?) => void (default process.exit, which ignores signal)
 * @param {boolean}  [opts.registerSignals] - forward SIGINT/SIGTERM to the child (default true)
 * @param {NodeJS.Process|EventEmitter} [opts.proc] - signal source (injected in tests; default process)
 * @returns {Promise<import('node:child_process').ChildProcess|undefined>} the child, or undefined if provisioning failed
 */
export async function runDirect({
  args,
  provision,
  spawn = realSpawn,
  stderr = process.stderr,
  onExit = (code) => process.exit(code),
  registerSignals = true,
  proc = process,
}) {
  const result = await provision();
  if (!result || !result.ok) {
    stderr.write(`[total-recall] could not provision engine: ${result ? result.error : 'unknown'}\n`);
    onExit(1);
    return undefined;
  }

  const child = spawn(result.binaryPath, args, { stdio: 'inherit' });

  if (registerSignals) {
    // Forward terminal signals to the child. NOTE: on Windows child.kill('SIGTERM')
    // maps to TerminateProcess (abrupt, no graceful shutdown) and named signals are
    // not real POSIX signals; but with stdio:'inherit' the child shares our console
    // group, so an interactive Ctrl+C reaches it directly regardless. Best-effort.
    for (const sig of ['SIGINT', 'SIGTERM']) {
      proc.on(sig, () => { try { child.kill(sig); } catch { /* best-effort */ } });
    }
  }

  // Settle exactly once. On a failed spawn (e.g. ENOENT) Node fires 'error' AND
  // then 'exit' with (null, null); without this guard the exit handler would
  // resolve that to onExit(0) and report success right after the error's onExit(1).
  let settled = false;
  const finish = (code, signal) => { if (settled) return; settled = true; onExit(code, signal); };

  child.on('error', (err) => {
    stderr.write(`[total-recall] failed to start engine: ${err.message}\n`);
    finish(1);
  });

  child.on('exit', (code, signal) => {
    // Mirror the child's fate: its numeric exit code when it exited normally;
    // for a signal death there is no portable exit code, so surface nonzero.
    finish(code == null ? (signal ? 1 : 0) : code, signal);
  });

  return child;
}
