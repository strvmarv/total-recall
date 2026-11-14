# Skills

total-recall can advertise custom skills at every `session_start` so your AI assistant knows which workflows are available. Skills are discovered from two places:

- **`~/.claude/skills/`** — the standard Claude Code user skills directory (always scanned)
- **`extra_dirs`** — additional directories you configure, scanned on every session start regardless of whether Cortex is available

Configure extra skill directories in `~/.total-recall/config.toml`:

```toml
[skills]
extra_dirs = [
  "~/my-custom-skills",
  "/path/to/shared/team-skills"
]
```

Paths can be absolute or `~/`-prefixed. Skills in `extra_dirs` are always advertised from disk — Cortex is not required.

**Uploading local skills to Cortex (`auto_import`):** In Cortex storage mode, scanning `~/.claude/skills/` for local `skill_get`/`skill_search` always happens. Whether those scanned skills are also *uploaded* to Cortex is a separate, opt-in setting:

```toml
[skills]
auto_import = true
```

Defaults to `false` — scanning and local skill lookup work either way, but nothing is uploaded unless you turn this on. This is opt-in because a host tool that already syncs skills down *from* Cortex (e.g. a Cortex skill-sync plugin populating `~/.claude/skills/`) would otherwise have those same skills scanned right back up as duplicate user-scoped entries on every session start.

**Skill format:** Each skill is either a single `.md` file or a directory containing a `SKILL.md` entry point. A minimal single-file skill:

```markdown
---
name: my-skill
description: Does something useful
---

Full skill content here...
```

A bundle (directory with supporting files) uses the same frontmatter in its `SKILL.md`, and can include scripts, templates, or reference files alongside it.

**Merge behavior:** When Cortex is configured and reachable, the session context block merges cortex-stored skills with locally-scanned `extra_dirs` skills, deduplicating by name (Cortex entries take precedence). When Cortex is unavailable or not configured, only local skills appear.
