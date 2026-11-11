# Ticket standard

What a good ticket is. Define writes the issue to this standard; the reviewer grades the preview
against it (see Review grades). It follows established practice, not one team's habits. Anything a
repository overrides is in Repository overrides.

## Is it one ticket

Test the change against INVEST: independent, negotiable, valuable, estimable, small, testable.

- A change that is not small or not independent is several tickets. Say so, ask the person which one
  to define first, and define only that one.
- Never create a parent or an epic.

## Problem before solution

- Say who is affected, what they cannot do today, and why it matters.
- Name the outcome wanted.
- Leave the mechanism to the spec (plan track) or the approach (small track). A plan-track ticket
  carries no engineering detail: no file names, function names, schema or design choices.

## Acceptance criteria

- Each criterion is observable and has a pass or fail answer.
- Use Given/When/Then where behaviour is involved, a plain checklist otherwise.
- Each criterion is one list item (`- [ ] ...`), one line of intent.
- No criterion such as "works well" or "is fast" without a number.
- The criteria sit in the marked region (see Markers).

## Scope

State what is in, what is out, and what is deliberately not being solved.

## Ready

Someone who was not in the conversation could start on the ticket without asking a question. If a
cold reader would ask one, the answer belongs in the ticket.

## Shape by type

Use these sections, in this order, when no template or profile says otherwise.

| Type | Sections, in order |
|---|---|
| Feature | Problem · Outcome · Acceptance criteria · Scope (in, out) · Context |
| Bug | What happens · Steps to reproduce · Expected · Actual · Environment and version · Impact · Acceptance criteria · Evidence |
| Chore | What · Why now · Acceptance criteria · Context |

A line break inside a paragraph is written `<br>`, never as two trailing spaces: trailing spaces are
not kept when the body is hashed.

## Title, labels and type

- **Title.** A plain statement of the outcome, at most 80 characters. Follow the repository's existing
  title convention when it has one; read it from recent issues.
- **Labels.** Use labels the repository already has (`bug`, `enhancement` and the like). Never create
  a label.
- **Type.** Set GitHub's issue type when the repository offers types; otherwise skip it.

## Repository overrides

Read `.github/ISSUE_TEMPLATE/` at `origin/<base>`, not from the working tree.

- A template exists for the type (a Markdown template or a form): its sections set the shape. The
  quality rules above (problem before solution, checkable criteria, scope, ready) still apply to the
  content.
- No template: the table in Shape by type sets the shape.
- The profile may name a template per type with `ticket.template.<type>`, or set
  `ticket.standard: off` to skip the shape and the quality rules. See `profile.md`.
- Whatever the shape, the body always has the marked acceptance-criteria region. When the template has
  no such section, append one.

## Review grades

In the plan track's spec loop and the small track's approach loop, the reviewer is given the ticket
preview and this standard, and grades the ticket.

| Finding | Grade |
|---|---|
| A required section is missing | major |
| A criterion cannot be checked | major |
| Engineering detail in a plan-track ticket | major |
| A title issue (over 80 characters, not an outcome, against the repository's convention) | minor |
| A length issue | minor |

## Markers

The body holds no machine block, only two kinds of region between HTML comments (GitHub does not
render them).

The criteria region, in both tracks:

```text
<!-- tr-develop:ac-begin -->
- [ ] Given a failed sync, when the user retries, then ...
<!-- tr-develop:ac-end -->
```

The approach region, in the small track only, in a collapsed block at the bottom of the ticket:

```text
<details><summary>Approach (small change, agreed at sign-off)</summary>

<!-- tr-develop:approach-begin -->
What changes, where, and the red-green cycles, in a few lines.
<!-- tr-develop:approach-end -->

</details>
```

- The marker strings are exactly `<!-- tr-develop:ac-begin -->`, `<!-- tr-develop:ac-end -->`,
  `<!-- tr-develop:approach-begin -->` and `<!-- tr-develop:approach-end -->`.
- Each region appears exactly once, begin before end. Anything else fails closed.
- A marker counts only outside a fenced code block. A marker or checkbox inside a fence is literal
  text. Fences follow CommonMark: backtick or tilde, closed only by a fence at least as long.
- The approach is at most 60 lines and 4,000 characters. One that does not fit belongs on the plan
  track: move it there.
