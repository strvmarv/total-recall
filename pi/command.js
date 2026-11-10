// pi/command.js — the /total-recall slash command for the pi extension.
//
// The command implements no subcommand itself. It forwards the request to the model,
// which already holds the command reference from the injected skills/commands/SKILL.md
// text. It is registered under two names: `total-recall` (short) and
// `total-recall:commands` (so the `/total-recall:commands compact` wording in that
// text and in the compaction nudge works verbatim).

const COMMANDS_HEADING = /^## Commands\s*$/;
const LEVEL2 = /^## /;
const LEVEL3 = /^### (\S+)/;
// Subcommands with no meaning on pi: `setup` configures Claude Code tool permissions,
// `update` keys off $CLAUDE_PLUGIN_ROOT and /plugin (on pi: `pi update`).
const EXCLUDED = new Set(['setup', 'update']);

/** First token of each `### ` heading under `## Commands`, de-duplicated, minus EXCLUDED. */
export function commandCompletions(skillText) {
  const out = [];
  let inCommands = false;
  for (const line of String(skillText ?? '').split(/\r?\n/)) {
    if (COMMANDS_HEADING.test(line)) { inCommands = true; continue; }
    if (inCommands && LEVEL2.test(line)) break;
    if (!inCommands) continue;
    const m = LEVEL3.exec(line);
    if (m && !EXCLUDED.has(m[1]) && !out.includes(m[1])) out.push(m[1]);
  }
  return out;
}

/** The user message the command sends: the arguments verbatim, `help` when blank. */
export function forwardText(args) {
  const a = String(args ?? '').trim();
  return `Run the total-recall command: ${a === '' ? 'help' : a}. Follow the Commands section of the total-recall instructions.`;
}

/**
 * Register `/total-recall` and `/total-recall:commands`.
 * `deps.hookable(ctx)` is false in subagent children and ephemeral sessions, where the
 * instructions were never injected; the command then only shows a notice.
 */
export function registerCommands(pi, { hookable, skillText }) {
  const completions = commandCompletions(skillText);
  const def = {
    description: 'total-recall memory: help, search, pin, forget, kb, compact, ...',
    getArgumentCompletions(prefix) {
      const p = String(prefix ?? '');
      if (/\s/.test(p)) return null;
      const items = completions.filter((c) => c.startsWith(p)).map((c) => ({ value: c, label: c }));
      return items.length > 0 ? items : null;
    },
    async handler(args, ctx) {
      if (!hookable(ctx)) {
        ctx.ui.notify('total-recall is not active in this session', 'warning');
        return;
      }
      pi.sendUserMessage(forwardText(args), ctx.isIdle() ? undefined : { deliverAs: 'followUp' });
    },
  };
  pi.registerCommand('total-recall', def);
  try {
    pi.registerCommand('total-recall:commands', def);
  } catch {
    // The alias is a convenience; never let it disable the main command.
  }
}
