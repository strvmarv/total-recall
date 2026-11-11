// pi/develop.js — the /develop slash command for the pi extension.
//
// The skill is not a pi manifest skill (a manifest skill would be advertised to subagent
// children too), so the extension registers /develop itself. The command implements no
// behaviour: it sends the model a message that names skills/develop/SKILL.md and carries
// the arguments verbatim at the end; the skill's Routing section decides what the arguments mean.

/**
 * The user message the command sends: read the skill, follow it, and here are the arguments
 * exactly as typed. Blank arguments say so, which the skill routes to its tutorial.
 */
export function developText(args, developSkillPath) {
  const raw = String(args ?? '');
  const head = `Run the develop skill: read ${developSkillPath} and follow it.`;
  if (raw.trim() === '') {
    return `${head}\nThe command was given no arguments: run the skill's Routing with an empty argument, which routes to the tutorial.`;
  }
  // The argument text is the last thing in the message, so no end marker is needed and nothing
  // inside the arguments can end it early.
  return `${head}\nThe argument text is everything after this line, to the end of the message, verbatim:\n${raw}`;
}

/**
 * Register `/develop`.
 * `deps.hookable(ctx)` is false in subagent children and ephemeral sessions; the command
 * then only shows a notice.
 */
export function registerDevelop(pi, { hookable, developSkillPath }) {
  try {
    pi.registerCommand('develop', {
      description: 'develop: define a change as a reviewed issue, or deliver one hands-off as a pull request (no arguments: tutorial)',
      async handler(args, ctx) {
        if (!hookable(ctx)) {
          ctx.ui.notify('develop is not active in this session', 'warning');
          return;
        }
        pi.sendUserMessage(developText(args, developSkillPath), ctx.isIdle() ? undefined : { deliverAs: 'followUp' });
      },
    });
  } catch {
    // The command is a convenience.
  }
}
