# Develop skill handoff

This file holds golden test fixtures for the zero-dependency scripts under
`skills/develop/scripts/`. Each fenced JSON block tagged `golden:<name>` below is read by
`golden.js` (`readGolden`, `goldenNames`) and consumed by the matching test file, so the
exact values a rule depends on live beside the prose that states it.

## Hashing and regions

`skills/develop/scripts/regions.js` normalizes ticket text and computes the hashes that pin a
`defined` issue (spec section 8). The goldens below are its test fixtures.

```json golden:normalize-pairs
[
  {
    "name": "glob-vs-dot",
    "a": "src/*.ts",
    "b": "src/.ts",
    "same": false
  },
  {
    "name": "underscore",
    "a": "max_retries",
    "b": "maxretries",
    "same": false
  },
  {
    "name": "angle-brackets",
    "a": "> 5",
    "b": "< 5",
    "same": false
  },
  {
    "name": "negative-number",
    "a": "-1",
    "b": "1",
    "same": false
  },
  {
    "name": "space-collapse",
    "a": "5 0",
    "b": "50",
    "same": false
  },
  {
    "name": "entity",
    "a": "&lt;",
    "b": "<",
    "same": false
  },
  {
    "name": "fenced-checkbox",
    "a": "```\n- [ ] item\n```",
    "b": "```\n- [x] item\n```",
    "same": false
  },
  {
    "name": "indented-fence-limit",
    "a": "para\n\n    ```\n    - [ ] item\n    ```\n",
    "b": "para\n\n    ```\n    - [x] item\n    ```\n",
    "same": true
  },
  {
    "name": "unordered-space",
    "a": "- [ ] a",
    "b": "- [x] a",
    "same": true
  },
  {
    "name": "ordered-marker",
    "a": "1. [ ] a",
    "b": "1. [X] a",
    "same": true
  },
  {
    "name": "nested-marker",
    "a": "  - [ ] nested",
    "b": "  - [x] nested",
    "same": true
  },
  {
    "name": "line-endings",
    "a": "line one\nline two\n",
    "b": "line one\r\nline two\r\n",
    "same": true
  },
  {
    "name": "trailing-whitespace",
    "a": "line one   \nline two\n\n\n",
    "b": "line one\nline two",
    "same": true
  }
]
```

```json golden:body-pairs
[
  {
    "name": "word-shift",
    "t1": "Fix login",
    "b1": "Crash on retry",
    "t2": "Fix login Crash",
    "b2": "on retry",
    "same": false
  },
  {
    "name": "identical",
    "t1": "Title",
    "b1": "Body text",
    "t2": "Title",
    "b2": "Body text",
    "same": true
  },
  {
    "name": "body-differs",
    "t1": "Title",
    "b1": "Body one",
    "t2": "Title",
    "b2": "Body two",
    "same": false
  }
]
```

```json golden:region-cases
[
  {
    "name": "ac-well-formed",
    "body": "Intro text.\n\n<!-- tr-develop:ac-begin -->\n- [ ] Given a thing, when done, then result.\n- [ ] Another criterion.\n<!-- tr-develop:ac-end -->\n\nTrailer text.\n",
    "kind": "ac",
    "expect": {
      "ok": true,
      "text": "- [ ] Given a thing, when done, then result.\n- [ ] Another criterion."
    }
  },
  {
    "name": "approach-well-formed",
    "body": "Context.\n\n<details><summary>Approach (small change, agreed at sign-off)</summary>\n\n<!-- tr-develop:approach-begin -->\nChange the retry loop in client.js to use exponential backoff.\n<!-- tr-develop:approach-end -->\n\n</details>\n",
    "kind": "approach",
    "expect": {
      "ok": true,
      "text": "Change the retry loop in client.js to use exponential backoff."
    }
  },
  {
    "name": "ac-crlf",
    "body": "Intro text.\r\n\r\n<!-- tr-develop:ac-begin -->\r\n- [ ] Given a thing, when done, then result.\r\n- [ ] Another criterion.\r\n<!-- tr-develop:ac-end -->\r\n\r\nTrailer text.\r\n",
    "kind": "ac",
    "expect": {
      "ok": true,
      "text": "- [ ] Given a thing, when done, then result.\n- [ ] Another criterion."
    }
  },
  {
    "name": "missing-begin",
    "body": "Intro\n\n<!-- tr-develop:ac-end -->\n\nTrailer\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "missing"
    }
  },
  {
    "name": "missing-end",
    "body": "Intro\n\n<!-- tr-develop:ac-begin -->\n- [ ] one\n\nTrailer\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "missing"
    }
  },
  {
    "name": "duplicate-two-ac-pairs",
    "body": "<!-- tr-develop:ac-begin -->\n- [ ] one\n<!-- tr-develop:ac-end -->\n\n<!-- tr-develop:ac-begin -->\n- [ ] two\n<!-- tr-develop:ac-end -->\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "duplicate"
    }
  },
  {
    "name": "order-end-before-begin",
    "body": "<!-- tr-develop:ac-end -->\n- [ ] one\n<!-- tr-develop:ac-begin -->\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "order"
    }
  },
  {
    "name": "fenced-only-missing",
    "body": "Intro\n\n```\n<!-- tr-develop:ac-begin -->\n- [ ] one\n<!-- tr-develop:ac-end -->\n```\n\nTrailer\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "missing"
    }
  },
  {
    "name": "fence-plus-real-ok",
    "body": "```\n<!-- tr-develop:ac-begin -->\nfake\n<!-- tr-develop:ac-end -->\n```\n\n<!-- tr-develop:ac-begin -->\n- [ ] real one\n<!-- tr-develop:ac-end -->\n",
    "kind": "ac",
    "expect": {
      "ok": true,
      "text": "- [ ] real one"
    }
  },
  {
    "name": "nested-fence-plus-real-ok",
    "body": "````\n```\n<!-- tr-develop:ac-begin -->\nfake\n<!-- tr-develop:ac-end -->\n```\n````\n\n<!-- tr-develop:ac-begin -->\n- [ ] real two\n<!-- tr-develop:ac-end -->\n",
    "kind": "ac",
    "expect": {
      "ok": true,
      "text": "- [ ] real two"
    }
  },
  {
    "name": "tilde-fence-only-missing",
    "body": "~~~\n<!-- tr-develop:ac-begin -->\n- [ ] one\n<!-- tr-develop:ac-end -->\n~~~\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "missing"
    }
  },
  {
    "name": "unclosed-fence-missing",
    "body": "```\n<!-- tr-develop:ac-begin -->\n- [ ] one\n<!-- tr-develop:ac-end -->\n",
    "kind": "ac",
    "expect": {
      "ok": false,
      "error": "missing"
    }
  },
  {
    "name": "json-fence-v1",
    "body": "<!-- tr-develop:ac-begin -->\n- [ ] Criterion uses config:\n```json\n{\"retries\": 3}\n```\n<!-- tr-develop:ac-end -->\n",
    "kind": "ac",
    "expect": {
      "ok": true,
      "text": "- [ ] Criterion uses config:\n```json\n{\"retries\": 3}\n```"
    }
  },
  {
    "name": "json-fence-v2",
    "body": "<!-- tr-develop:ac-begin -->\n- [ ] Criterion uses config:\n```json\n{\"retries\": 4}\n```\n<!-- tr-develop:ac-end -->\n",
    "kind": "ac",
    "expect": {
      "ok": true,
      "text": "- [ ] Criterion uses config:\n```json\n{\"retries\": 4}\n```"
    }
  },
  {
    "name": "approach-over-limit",
    "body": "Context.\n\n<details><summary>Approach (small change, agreed at sign-off)</summary>\n\n<!-- tr-develop:approach-begin -->\n```\nline 0\nline 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\nline 11\nline 12\nline 13\nline 14\nline 15\nline 16\nline 17\nline 18\nline 19\nline 20\nline 21\nline 22\nline 23\nline 24\nline 25\nline 26\nline 27\nline 28\nline 29\nline 30\nline 31\nline 32\nline 33\nline 34\nline 35\nline 36\nline 37\nline 38\nline 39\nline 40\nline 41\nline 42\nline 43\nline 44\nline 45\nline 46\nline 47\nline 48\nline 49\nline 50\nline 51\nline 52\nline 53\nline 54\nline 55\nline 56\nline 57\nline 58\nline 59\nline 60\nline 61\nline 62\nline 63\nline 64\n```\n<!-- tr-develop:approach-end -->\n\n</details>\n",
    "kind": "approach",
    "expect": {
      "ok": true,
      "text": "```\nline 0\nline 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\nline 11\nline 12\nline 13\nline 14\nline 15\nline 16\nline 17\nline 18\nline 19\nline 20\nline 21\nline 22\nline 23\nline 24\nline 25\nline 26\nline 27\nline 28\nline 29\nline 30\nline 31\nline 32\nline 33\nline 34\nline 35\nline 36\nline 37\nline 38\nline 39\nline 40\nline 41\nline 42\nline 43\nline 44\nline 45\nline 46\nline 47\nline 48\nline 49\nline 50\nline 51\nline 52\nline 53\nline 54\nline 55\nline 56\nline 57\nline 58\nline 59\nline 60\nline 61\nline 62\nline 63\nline 64\n```"
    }
  }
]
```

```json golden:fence-cases
[
  {
    "name": "nested-four-over-three",
    "text": "before\n````\n```\ncontent\n````\nafter",
    "lines": [
      [
        1,
        "before"
      ],
      [
        6,
        "after"
      ]
    ]
  },
  {
    "name": "tilde-fence",
    "text": "before\n~~~\ntilde content\n~~~\nafter",
    "lines": [
      [
        1,
        "before"
      ],
      [
        5,
        "after"
      ]
    ]
  },
  {
    "name": "indented-fence-three-spaces",
    "text": "before\n   ```\ncode\n   ```\nafter",
    "lines": [
      [
        1,
        "before"
      ],
      [
        5,
        "after"
      ]
    ]
  },
  {
    "name": "indented-line-four-spaces-not-fence",
    "text": "before\n    ```\ncode\nafter",
    "lines": [
      [
        1,
        "before"
      ],
      [
        2,
        "    ```"
      ],
      [
        3,
        "code"
      ],
      [
        4,
        "after"
      ]
    ]
  }
]
```

```json golden:limit-cases
[
  {
    "name": "sixty-lines",
    "text": "line\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline",
    "ok": true
  },
  {
    "name": "sixty-one-lines",
    "text": "line\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline\nline",
    "ok": false
  },
  {
    "name": "four-thousand-chars",
    "text": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "ok": true
  },
  {
    "name": "four-thousand-one-chars",
    "text": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "ok": false
  }
]
```

```json golden:neutralize-cases
[
  {
    "name": "ac-begin-marker",
    "in": "Notes.\n\n<!-- tr-develop:ac-begin -->\n- [ ] item\n<!-- tr-develop:ac-end -->\n",
    "out": "Notes.\n\n&lt;!-- tr-develop:ac-begin -->\n- [ ] item\n&lt;!-- tr-develop:ac-end -->\n"
  },
  {
    "name": "checkpoint-line",
    "in": "tr-develop/v1 phase=kickoff branch=feat/x\n",
    "out": "tr&#8209;develop/v1 phase=kickoff branch=feat/x\n"
  }
]
```

## Checkpoints

`skills/develop/scripts/checkpoints.js` owns the checkpoint line that a progress comment carries
(spec section 10): one line, `tr-develop/v1 phase=<phase> key=value ...`, alone inside a fence,
after a short human-readable summary. The goldens at the end of this section are its test
fixtures.

**The line.** After every `\r` is removed, a comment body is split into lines and each line is
trimmed of whitespace, NBSP (U+00A0) and a BOM (U+FEFF). Exactly one trimmed line may match

```text
^tr-develop/v1 phase=(defined|reapproved|kickoff|plan-approved|tasks|final-approved|pr-open|blocked|closed)( [a-zA-Z][a-zA-Z0-9.]*=\S+)*$
```

No match is `no-line`; two or more are `multiple-lines` (a quoted copy of the line in the summary
counts, so a repair never guesses which one is real). A key that repeats is refused as `no-line`
rather than guessed. The fields are the `key=value` tokens after `phase`, split at the first `=`.

**Patterns.** Branch `^(feat|fix)/[a-z0-9][a-z0-9-]{0,62}$` (`-` is allowed on `blocked` only); spec
`^docs/superpowers/specs/[A-Za-z0-9._-]+\.md$`; plan `^docs/superpowers/plans/[A-Za-z0-9._-]+\.md$`;
`head`, `planSha` and `approvedSha` are 40 lowercase hex; `bodyHash` and `acHash` are 64 lowercase
hex; counters (`tasksDone`, `cyclesDone`, `deferred`, `rounds.*`) are `^\d+$`, so `0` is valid; `track`
is `plan` or `small`; `review` is `agent` or `self`; `stage` is one of `read`, `workspace`, `plan`,
`build`, `final`, `finish`; `blocker` is `1` to `6`; `gh` and `definedBy` pass `isLogin`
(`^[A-Za-z0-9](?:[A-Za-z0-9_-]{0,38})$`, defined once in `checkpoints.js`); `repo` equals the context
repository case-insensitively and `base` equals the context base, on `defined` and `reapproved`;
`pr` is the prefix `https://github.com/<repo>/pull/` (compared case-insensitively, so a `.` in a
repository name is literal) followed by `[1-9]\d{0,8}`.

**Required fields.** "plan:" means the plan track only, "small:" the small track only. `plan` and
`planSha` are optional on `defined`, `reapproved` and `kickoff`, but present together or absent
together on every phase. Fields not listed are optional; a listed key that is present is always
checked.

| Phase | Required |
| --- | --- |
| `defined` | track, repo, base, branch, bodyHash, acHash, definedBy, definedAt; plan: spec, approvedSha |
| `reapproved` | everything `defined` requires, plus head, review, gh, deferred; never `rounds.final` or `pr` |
| `kickoff` | track, branch, head, review, gh; plan: spec, approvedSha |
| `plan-approved` | track, branch, head, review, gh, spec, approvedSha, plan, planSha, rounds.plan |
| `tasks` | track, branch, head, review, gh, deferred; plan: spec, approvedSha, plan, planSha, tasksDone; small: cyclesDone |
| `final-approved` | track, branch, head, review, gh, deferred, rounds.final; plan: spec, approvedSha, plan, planSha, tasksDone |
| `pr-open` | track, branch, head, review, gh, pr, deferred; plan: spec, approvedSha, plan, planSha, tasksDone |
| `blocked` | branch, stage, blocker |
| `closed` | branch, head, gh, pr |

**Reachability.** `head` and `planSha` must be reachable from the branch tip on every phase except
`closed` (after a merge the branch may be gone, so `closed` only needs a 40-hex `head`);
`approvedSha` must be reachable on `defined` and `reapproved`. The caller computes `reachable` with
`git merge-base --is-ancestor` first and passes the list as data.

**Carry-forward** is a separate write-time check, never applied when reading history:
`node checkpoints.js carry <jsonFile>` takes `{checkpoint, prior}`, where `prior` is the latest
counted progress checkpoint taken from `select` **before** posting, or `null` when there is none.
`prior` is a required key; a missing one is `{"ok":false,"error":"bad-input"}`. Before a `kickoff`
or `reapproved` is posted, every one of `plan`, `planSha`, `tasksDone`, `cyclesDone`, `rounds.plan`
and `deferred` that `prior` carries must also be present; on a `kickoff` `rounds.final` is carried
too, since a new executor resumes the final loop. A `reapproved` may replace a value but not omit it,
and never carries `rounds.final`: a re-approval invalidates the final loop. `pr` is never carried.

**Writing a comment.** `node checkpoints.js format <jsonFile>` takes
`{phase, fields, summaryFile, outFile, lineFile}`, writes the whole comment to `outFile` (the
summary, a blank line, then the line inside a fence one backtick longer than any run in the summary)
and the bare line to `lineFile`, both UTF-8 without a BOM, and prints `{line}`. Keys are written
`branch` first, then in the order `track`, `repo`, `base`, `bodyHash`, `acHash`, `definedBy`,
`definedAt`, `spec`, `approvedSha`, `head`, `review`, `gh`, `plan`, `planSha`, `rounds.plan`,
`tasksDone`, `cyclesDone`, `deferred`, `rounds.final`, `pr`, `stage`, `blocker`, then any unknown key
sorted. It refuses with `{"ok":false,"error":"summary-has-checkpoint-line"}`, writing nothing, when
any summary line matches the grammar after trimming, and with `bad-value` (and the key) for an
empty value or one that holds whitespace.

**Read-back.** After posting, fetch the comment and run
`node checkpoints.js readback <fetchedCommentFile> <lineFile>`: `{"ok":true}` only when the fetched
text yields exactly the line in `lineFile`, else `{"ok":false,"error":"no-line"|"multiple-lines"|"mismatch"}`.
The comparison is the script's, not the reader's.

Goldens: `parse-cases` (`{name, body, expect}`), `validate-cases` (`{name, cp, ctx, reachable, expect}`),
`carry-cases` (`{name, checkpoint, prior, expect}`; a case with no `prior` key is the missing-key case) and
`readback-cases` (`{name, line, fetched, expect}`).

```json golden:parse-cases
[
  {"name":"summary text then a fenced line","body":"Kicked off the work.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n```\n","expect":{"ok":true,"phase":"kickoff"}},
  {"name":"CRLF body with trailing spaces after the line","body":"Summary\r\n\r\n```\r\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small   \r\n```\r\n","expect":{"ok":true,"phase":"kickoff"}},
  {"name":"two matching lines","body":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n","expect":{"ok":false,"error":"multiple-lines"}},
  {"name":"the line quoted in the summary plus the real one in the fence","body":"I will post:\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n```\n","expect":{"ok":false,"error":"multiple-lines"}},
  {"name":"no line","body":"Just a summary, no checkpoint.\n","expect":{"ok":false,"error":"no-line"}},
  {"name":"empty body","body":"","expect":{"ok":false,"error":"no-line"}},
  {"name":"unknown phase","body":"tr-develop/v1 phase=deploy branch=feat/x\n","expect":{"ok":false,"error":"no-line"}},
  {"name":"leading space is trimmed","body":"   tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n","expect":{"ok":true,"phase":"kickoff"}},
  {"name":"NBSP-prefixed line is trimmed","body":"\u00a0tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n","expect":{"ok":true,"phase":"kickoff"}},
  {"name":"BOM-prefixed line is trimmed","body":"\ufefftr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n","expect":{"ok":true,"phase":"kickoff"}},
  {"name":"token without an equals sign","body":"tr-develop/v1 phase=kickoff branch\n","expect":{"ok":false,"error":"no-line"}},
  {"name":"a blockquoted line is not a line","body":"> tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n","expect":{"ok":false,"error":"no-line"}},
  {"name":"text after the line on the same line","body":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small and more words\n","expect":{"ok":false,"error":"no-line"}},
  {"name":"rounds.plan=0 and definedAt","body":"tr-develop/v1 phase=defined track=small rounds.plan=0 definedAt=2026-11-10T12:34:56Z\n","expect":{"ok":true,"phase":"defined"}},
  {"name":"a repeated key is refused, not guessed","body":"tr-develop/v1 phase=kickoff branch=feat/x branch=feat/y\n","expect":{"ok":false,"error":"no-line"}},
  {"name":"the line plus a second one with a repeated key","body":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\ntr-develop/v1 phase=tasks head=a head=b\n","expect":{"ok":false,"error":"multiple-lines"}}
]
```

```json golden:validate-cases
[
  {"name":"defined plan track","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"defined small track","cp":{"phase":"defined","fields":{"track":"small","repo":"strvmarv/total-recall","base":"main","branch":"fix/y","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"strvmarv","definedAt":"2026-11-10T12:34:56Z"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"defined repo differs only in case","cp":{"phase":"defined","fields":{"track":"plan","repo":"StrVmarv/Total-Recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"reapproved","cp":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"2","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"kickoff plan track","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"kickoff small track","cp":{"phase":"kickoff","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"kickoff carrying plan and planSha","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"2"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"plan-approved with rounds.plan=0","cp":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","rounds.plan":"0"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"tasks plan track","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"tasks small track","cp":{"phase":"tasks","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv","deferred":"1","cyclesDone":"0"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"final-approved","cp":{"phase":"final-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.final":"2","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"pr-open","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/42","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"blocked","cp":{"phase":"blocked","fields":{"branch":"feat/x","stage":"build","blocker":"4"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"blocked with branch=-","cp":{"phase":"blocked","fields":{"branch":"-","stage":"read","blocker":"1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"closed","cp":{"phase":"closed","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/42"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"closed head not reachable (branch deleted after merge)","cp":{"phase":"closed","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/42"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":[],"expect":{"ok":true}},
  {"name":"closed with planSha not reachable","cp":{"phase":"closed","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/42","planSha":"ffffffffffffffffffffffffffffffffffffffff","plan":"docs/superpowers/plans/2026-11-10-x.md"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":[],"expect":{"ok":true}},
  {"name":"pr repository name with a dot matches literally","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/acme/web.app/pull/7","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"acme/web.app","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"pr host and repo compare case-insensitively","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://GitHub.com/STRVMARV/Total-Recall/pull/9","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"kickoff approvedSha need not be reachable","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"ffffffffffffffffffffffffffffffffffffffff"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"unknown field is ignored","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","note":"anything"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":true}},
  {"name":"defined missing bodyHash","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"bodyHash"}},
  {"name":"defined plan track missing spec","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"spec"}},
  {"name":"defined plan track missing approvedSha","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"approvedSha"}},
  {"name":"defined missing track","cp":{"phase":"defined","fields":{"repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"track"}},
  {"name":"reapproved missing deferred","cp":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","rounds.plan":"2","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"deferred"}},
  {"name":"reapproved missing head","cp":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"2","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"head"}},
  {"name":"kickoff missing head","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"head"}},
  {"name":"kickoff missing track","cp":{"phase":"kickoff","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"track"}},
  {"name":"kickoff with planSha but no plan","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"plan"}},
  {"name":"kickoff with plan but no planSha","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"planSha"}},
  {"name":"plan-approved missing rounds.plan","cp":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"rounds.plan"}},
  {"name":"plan-approved missing planSha","cp":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","rounds.plan":"0"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"planSha"}},
  {"name":"tasks plan track missing tasksDone","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"tasksDone"}},
  {"name":"tasks plan track missing plan","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"plan"}},
  {"name":"tasks small track missing cyclesDone","cp":{"phase":"tasks","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv","deferred":"1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"cyclesDone"}},
  {"name":"final-approved missing rounds.final","cp":{"phase":"final-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"rounds.final"}},
  {"name":"pr-open missing pr","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"pr"}},
  {"name":"blocked missing stage","cp":{"phase":"blocked","fields":{"branch":"feat/x","blocker":"4"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"stage"}},
  {"name":"blocked missing blocker","cp":{"phase":"blocked","fields":{"branch":"feat/x","stage":"build"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"blocker"}},
  {"name":"closed missing pr","cp":{"phase":"closed","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gh":"strvmarv"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"pr"}},
  {"name":"closed missing gh","cp":{"phase":"closed","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pr":"https://github.com/strvmarv/total-recall/pull/42"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"missing","key":"gh"}},
  {"name":"branch=- on kickoff","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"-","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"branch"}},
  {"name":"branch outside the pattern","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"chore/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"branch"}},
  {"name":"branch with an upper-case letter","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/X","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"branch"}},
  {"name":"blocker=7","cp":{"phase":"blocked","fields":{"branch":"feat/x","stage":"build","blocker":"7"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"blocker"}},
  {"name":"blocker=0","cp":{"phase":"blocked","fields":{"branch":"feat/x","stage":"build","blocker":"0"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"blocker"}},
  {"name":"stage=deploy","cp":{"phase":"blocked","fields":{"branch":"feat/x","stage":"deploy","blocker":"4"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"stage"}},
  {"name":"track=other","cp":{"phase":"kickoff","fields":{"track":"other","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"track"}},
  {"name":"review=other","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"other","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"review"}},
  {"name":"head 39 hex","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"head"}},
  {"name":"head not reachable","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"head"}},
  {"name":"head upper-case hex","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"head"}},
  {"name":"blocked head not reachable","cp":{"phase":"blocked","fields":{"branch":"feat/x","stage":"build","blocker":"4","head":"ffffffffffffffffffffffffffffffffffffffff"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"head"}},
  {"name":"planSha not reachable","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"planSha"}},
  {"name":"planSha 41 hex","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"planSha"}},
  {"name":"approvedSha not reachable on defined","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"],"expect":{"ok":false,"error":"bad-value","key":"approvedSha"}},
  {"name":"approvedSha 40 chars but not hex","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"approvedSha"}},
  {"name":"bodyHash 63 hex","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"ddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"bodyHash"}},
  {"name":"acHash 65 hex","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"acHash"}},
  {"name":"plan path outside docs/superpowers/plans","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/specs/x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"plan"}},
  {"name":"plan path with a subdirectory","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/sub/x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"plan"}},
  {"name":"spec path outside docs/superpowers/specs","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/plans/x.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"spec"}},
  {"name":"rounds.plan negative","cp":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","rounds.plan":"-1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"rounds.plan"}},
  {"name":"rounds.final not a number","cp":{"phase":"final-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.final":"two","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"rounds.final"}},
  {"name":"deferred not a number","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"1.5","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"deferred"}},
  {"name":"tasksDone empty","cp":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":""}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"tasksDone"}},
  {"name":"gh=x;y","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"x;y","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"gh"}},
  {"name":"gh starts with a hyphen","cp":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"-lead","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"gh"}},
  {"name":"definedBy=$(id)","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"$(id)","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"definedBy"}},
  {"name":"repo differs from the context","cp":{"phase":"defined","fields":{"track":"plan","repo":"other/repo","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"repo"}},
  {"name":"base differs from the context","cp":{"phase":"defined","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"develop","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"base"}},
  {"name":"pr in another repository","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/other/repo/pull/42","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"pr lookalike with a letter where the repository has a dot","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/acme/webXapp/pull/5","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"acme/web.app","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"pr number with a leading zero","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/042","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"pr number zero","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/0","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"pr number of ten digits","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/1234567890","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"pr with a trailing path","cp":{"phase":"pr-open","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/pull/42/files","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"pr that is an issue URL","cp":{"phase":"closed","fields":{"branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gh":"strvmarv","pr":"https://github.com/strvmarv/total-recall/issues/42"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"reapproved carrying rounds.final","cp":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"2","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1","rounds.final":"1"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"rounds.final"}},
  {"name":"reapproved carrying pr","cp":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"2","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1","pr":"https://github.com/strvmarv/total-recall/pull/42"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"pr"}},
  {"name":"unknown phase","cp":{"phase":"deploy","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"ctx":{"repo":"strvmarv/total-recall","base":"main"},"reachable":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccccccccccc"],"expect":{"ok":false,"error":"bad-value","key":"phase"}}
]
```

```json golden:carry-cases
[
  {"name":"reapproved omits tasksDone that prior carried","checkpoint":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"2","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},"prior":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"expect":{"ok":false,"error":"missing","key":"tasksDone"}},
  {"name":"reapproved omits plan and planSha that prior carried","checkpoint":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"2","tasksDone":"1"}},"prior":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"expect":{"ok":false,"error":"missing","key":"plan"}},
  {"name":"reapproved after final-approved without rounds.final (the final loop is invalidated)","checkpoint":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"3","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1"}},"prior":{"phase":"final-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.final":"2","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3","rounds.plan":"3"}},"expect":{"ok":true}},
  {"name":"reapproved replaces rounds.plan with a new value","checkpoint":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.plan":"5","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1"}},"prior":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","rounds.plan":"2"}},"expect":{"ok":true}},
  {"name":"reapproved omits rounds.plan that prior carried","checkpoint":{"phase":"reapproved","fields":{"track":"plan","repo":"strvmarv/total-recall","base":"main","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","acHash":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","definedBy":"jane-doe_acme","definedAt":"2026-11-10T12:34:56Z","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"1"}},"prior":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","rounds.plan":"0"}},"expect":{"ok":false,"error":"missing","key":"rounds.plan"}},
  {"name":"kickoff omits planSha that prior carried","checkpoint":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"prior":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"expect":{"ok":false,"error":"missing","key":"plan"}},
  {"name":"kickoff omits cyclesDone and deferred that prior carried","checkpoint":{"phase":"kickoff","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv"}},"prior":{"phase":"tasks","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv","deferred":"1","cyclesDone":"0"}},"expect":{"ok":false,"error":"missing","key":"cyclesDone"}},
  {"name":"kickoff carries cyclesDone but omits deferred","checkpoint":{"phase":"kickoff","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv","cyclesDone":"0"}},"prior":{"phase":"tasks","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv","deferred":"1","cyclesDone":"0"}},"expect":{"ok":false,"error":"missing","key":"deferred"}},
  {"name":"kickoff carries everything prior carried","checkpoint":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3","deferred":"0"}},"prior":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"expect":{"ok":true}},
  {"name":"kickoff with a null prior and no plan fields","checkpoint":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"prior":null,"expect":{"ok":true}},
  {"name":"kickoff after a blocked-only history (prior is null)","checkpoint":{"phase":"kickoff","fields":{"track":"small","branch":"fix/y","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"self","gh":"strvmarv"}},"prior":null,"expect":{"ok":true}},
  {"name":"a tasks checkpoint is never carry-checked","checkpoint":{"phase":"tasks","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3"}},"prior":{"phase":"plan-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","rounds.plan":"0"}},"expect":{"ok":true}},
  {"name":"kickoff omits rounds.final that a final-approved prior carried","checkpoint":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3","rounds.plan":"3","deferred":"0"}},"prior":{"phase":"final-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.final":"2","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3","rounds.plan":"3"}},"expect":{"ok":false,"error":"missing","key":"rounds.final"}},
  {"name":"kickoff carries rounds.final that a final-approved prior carried","checkpoint":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3","rounds.plan":"3","deferred":"0","rounds.final":"2"}},"prior":{"phase":"final-approved","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","deferred":"0","rounds.final":"2","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc","plan":"docs/superpowers/plans/2026-11-10-x.md","planSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tasksDone":"3","rounds.plan":"3"}},"expect":{"ok":true}},
  {"name":"a call with no prior key is bad-input","checkpoint":{"phase":"kickoff","fields":{"track":"plan","branch":"feat/x","head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","review":"agent","gh":"strvmarv","spec":"docs/superpowers/specs/2026-11-10-x-design.md","approvedSha":"cccccccccccccccccccccccccccccccccccccccc"}},"expect":{"ok":false,"error":"bad-input"}}
]
```

```json golden:readback-cases
[
  {"name":"fetched equals the posted comment","line":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small","fetched":"Resumed on the branch.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n```\n","expect":{"ok":true}},
  {"name":"fetched after a web edit: CRLF and trailing spaces","line":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small","fetched":"Resumed on the branch.  \r\n  \r\n```  \r\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small  \r\n```  \r\n","expect":{"ok":true}},
  {"name":"fetched has a second matching line","line":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small","fetched":"Resumed on the branch.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n```\n\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small\n","expect":{"ok":false,"error":"multiple-lines"}},
  {"name":"fetched line altered","line":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small","fetched":"Resumed on the branch.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=self gh=strvmarv track=small\n```\n","expect":{"ok":false,"error":"mismatch"}},
  {"name":"fetched has a different key order","line":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small","fetched":"```\ntr-develop/v1 phase=kickoff gh=strvmarv branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent track=small\n```\n","expect":{"ok":false,"error":"mismatch"}},
  {"name":"fetched lost the line","line":"tr-develop/v1 phase=kickoff branch=feat/x head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa review=agent gh=strvmarv track=small","fetched":"Resumed on the branch.\n","expect":{"ok":false,"error":"no-line"}}
]
```

## Trust

`skills/develop/scripts/counted.js`, `pages.js` and `history.js` decide which comments and which text a run may
act on (spec sections 9 and 11). The goldens at the end of this section are their test fixtures.

**Write access.** A login has write access when the permission endpoint answers with a `role_name` of
`admin`, `maintain` or `write`, or a `permission` of `admin` or `write`. A 404 means no write access. It
leaves no output file, because `ghpin.js run` removes its `out` file on a failure, so the CLIs read "no file
and `httpStatus` 404" as no write access and any other missing file (403, 5xx, a rate limit, a missing
status) as `unavailable`: stop with blocker 3, never a guess. (A caller without collaborator access gets a
403 for every target, even a login that does not exist, so a 403 is never read as "no access".)
`counted.js write-access <permFile> [httpStatus]` answers for one login.
`counted.js permissions <manifestFile> <outFile>` takes `[{login, file, httpStatus}]` and writes the
`{<login>: perm|null}` map that `select` reads. It merges into an existing `outFile` of the same run, so the
first pass creates the map and later passes only add logins; the map lives in the run's scratch directory
(`ghpin.js scratch`), which a resumed session replaces, and is never written by hand. A missing
`permissionsFile` is `{}`; presence is tested with `Object.hasOwn`, so a login named `constructor` or
`toString` is looked up like any other.

**Which comments count.** `counted.js select <rawCommentsFile> <permissionsFile>` flattens the raw paginated
GraphQL output (`pages.js` `readPages` splits the pages; a page with `errors`, `issue: null`, no
`comments.nodes` or a last page with `hasNextPage` true is an error, so the run stops with blocker 3).
A comment counts when its author is a `User` with write access, its editor (if any) is too, an editor that is
null with `lastEditedAt` set does not count, and it is not minimized. A Bot, Mannequin or Organization
never counts and its login is never looked up. `needPermissions` lists each valid `User` login (author or
editor) of a comment that carries exactly one checkpoint line and is absent from the map; a `null` entry
means "looked up, no write access" and is never listed. `complete` is false while it is non-empty, and the
caller fetches those permissions, runs `permissions`, and selects again; it never acts on `pin`, `progress` or
a route from an incomplete pass. `counted` and `checkpoints` hold the counted comments that carry a
checkpoint line; `latest` is the latest counted checkpoint of any phase (`defined` and `blocked` included,
with its fields); `pin` is the latest counted `defined` or `reapproved`; `progress` is the latest counted
`kickoff`, `plan-approved`, `tasks`, `final-approved`, `pr-open`, `closed` or `reapproved`.

**Recovering the old text.** `history.js find <editsRawFile> <renamesRawFile> <issueJsonFile>
<pinnedBodyHash> <outDir>` tries the current body, then each revision newest first (a null `diff` or a
`deletedAt` is skipped), against every title in the rename chain, and writes `old.title` and `old.body` to
`outDir` when a pair hashes to the pin. `{found:false}` means original unavailable: show only the current
text and say the original could not be recovered. A failed parse is `{ok:false}`: stop with blocker 3.

**Fetch commands.** Every read or write below runs through `ghpin.js run` with an `out` file (text
never passes through a shell); `<skill>` is the skill directory.

```json golden:perm-cases
[
  {"name":"admin role","perm":{"role_name":"admin"},"expect":true},
  {"name":"maintain role","perm":{"role_name":"maintain"},"expect":true},
  {"name":"write role","perm":{"role_name":"write"},"expect":true},
  {"name":"custom role on write (permission only)","perm":{"permission":"write"},"expect":true},
  {"name":"permission admin","perm":{"permission":"admin"},"expect":true},
  {"name":"both fields as GitHub returns them","perm":{"permission":"write","role_name":"write"},"expect":true},
  {"name":"triage role with read permission","perm":{"role_name":"triage","permission":"read"},"expect":false},
  {"name":"permission none","perm":{"permission":"none"},"expect":false},
  {"name":"read role","perm":{"role_name":"read","permission":"read"},"expect":false},
  {"name":"null (looked up, no access)","perm":null,"expect":false},
  {"name":"empty object","perm":{},"expect":false}
]
```

```json golden:login-cases
[
  {"login":"jane-doe_acme","expect":true},
  {"login":"a","expect":true},
  {"login":"a-b_c","expect":true},
  {"login":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","expect":true},
  {"login":"constructor","expect":true},
  {"login":"x; rm -rf /","expect":false},
  {"login":"$(id)","expect":false},
  {"login":"-lead","expect":false},
  {"login":"_lead","expect":false},
  {"login":"","expect":false},
  {"login":"octocat\n","expect":false},
  {"login":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","expect":false},
  {"login":"__proto__","expect":false}
]
```

```json golden:comment-count-cases
[
  {"name":"a writer author counts","comment":{"id":"c1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":true},
  {"name":"a writer who edited their own comment counts","comment":{"id":"c1","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"strvmarv","editorType":"User","lastEditedAt":"2026-11-10T10:30:00Z","isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":true},
  {"name":"a writer author with a writer editor counts","comment":{"id":"c1","createdAt":"2026-11-10T10:03:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"jane-doe_acme","editorType":"User","lastEditedAt":"2026-11-10T10:30:00Z","isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":true},
  {"name":"a Bot author whose login is in writers does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:04:00Z","authorLogin":"strvmarv","authorType":"Bot","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a Mannequin author does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:05:00Z","authorLogin":"strvmarv","authorType":"Mannequin","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"an Organization author does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:06:00Z","authorLogin":"strvmarv","authorType":"Organization","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a writer author with a Bot editor does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:07:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"jane-doe_acme","editorType":"Bot","lastEditedAt":"2026-11-10T10:30:00Z","isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a non-writer author does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:08:00Z","authorLogin":"outsider","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a writer author with a non-writer editor does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:09:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"outsider","editorType":"User","lastEditedAt":"2026-11-10T10:30:00Z","isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a null editor with lastEditedAt set (deleted editor account) does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:10:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":"2026-11-10T10:30:00Z","isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a minimized comment does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:11:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":true,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a null author does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:12:00Z","authorLogin":null,"authorType":null,"editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv"],"expect":false},
  {"name":"a hostile author login does not count even if listed as a writer","comment":{"id":"c1","createdAt":"2026-11-10T10:13:00Z","authorLogin":"x; rm -rf /","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"x"},"writers":["x; rm -rf /"],"expect":false},
  {"name":"a writer author with a hostile editor login does not count","comment":{"id":"c1","createdAt":"2026-11-10T10:14:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"$(id)","editorType":"User","lastEditedAt":"2026-11-10T10:30:00Z","isMinimized":false,"body":"x"},"writers":["jane-doe_acme","strvmarv","$(id)"],"expect":false}
]
```

```json golden:pin-cases
[
  {"name":"an outsider's forged defined after a writer's is ignored","comments":[{"id":"w-def","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small\n```\n"},{"id":"o-def","createdAt":"2026-11-10T10:02:00Z","authorLogin":"outsider","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small\n```\n"}],"writers":["strvmarv"],"expectPin":"w-def","expectProgress":null},
  {"name":"a later reapproved becomes the pin and is also progress","comments":[{"id":"d1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small\n```\n"},{"id":"k1","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"r1","createdAt":"2026-11-10T10:03:00Z","authorLogin":"jane-doe_acme","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for reapproved.\n\n```\ntr-develop/v1 phase=reapproved branch=feat/x track=small\n```\n"}],"writers":["strvmarv","jane-doe_acme"],"expectPin":"r1","expectProgress":"r1"},
  {"name":"blocked is never progress","comments":[{"id":"k1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"b1","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for blocked.\n\n```\ntr-develop/v1 phase=blocked branch=feat/x stage=build blocker=3\n```\n"}],"writers":["strvmarv"],"expectPin":null,"expectProgress":"k1"},
  {"name":"a lone kickoff with no pin gives no pin and a progress id","comments":[{"id":"k1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"writers":["strvmarv"],"expectPin":null,"expectProgress":"k1"},
  {"name":"a minimized later pin is retired, the earlier one governs again","comments":[{"id":"d1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small\n```\n"},{"id":"d2","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":true,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small\n```\n"}],"writers":["strvmarv"],"expectPin":"d1","expectProgress":null},
  {"name":"defined is the pin but never progress; the latest progress wins","comments":[{"id":"d1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small\n```\n"},{"id":"k1","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"t1","createdAt":"2026-11-10T10:03:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for tasks.\n\n```\ntr-develop/v1 phase=tasks branch=feat/x track=small\n```\n"}],"writers":["strvmarv"],"expectPin":"d1","expectProgress":"t1"},
  {"name":"ordinary comments give neither a pin nor progress","comments":[{"id":"x1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Looks good to me."}],"writers":["strvmarv"],"expectPin":null,"expectProgress":null},
  {"name":"a comment with two checkpoint lines is invalid as a whole","comments":[{"id":"k1","createdAt":"2026-11-10T10:01:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"k2","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for closed.\n\n```\ntr-develop/v1 phase=closed branch=feat/x x=1\n```\n\ntr-develop/v1 phase=closed branch=feat/x\n"}],"writers":["strvmarv"],"expectPin":null,"expectProgress":"k1"}
]
```

```json golden:flatten-cases
[
  {"name":"two pages: escaped quote, string ending in a backslash, quoted braces, null editor, Bot author, null author, minimized","fixture":"comments-two-pages.json","expect":[{"id":"IC_1","createdAt":"2026-11-10T09:00:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Quote: 5\" pipe }"},{"id":"IC_2","createdAt":"2026-11-10T09:01:00Z","authorLogin":"jane-doe_acme","authorType":"User","editorLogin":"strvmarv","editorType":"User","lastEditedAt":"2026-11-10T09:05:00Z","isMinimized":false,"body":"Path C:\\"},{"id":"IC_3","createdAt":"2026-11-10T09:02:00Z","authorLogin":"dependabot[bot]","authorType":"Bot","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"a \"{\" b and a } stray"},{"id":"IC_4","createdAt":"2026-11-10T09:03:00Z","authorLogin":null,"authorType":null,"editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"ghost comment"},{"id":"IC_5","createdAt":"2026-11-10T09:04:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":true,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"IC_6","createdAt":"2026-11-10T09:06:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":"2026-11-10T09:07:00Z","isMinimized":false,"body":"line one\r\nline \"two\"\\n literal backslash-n \\\\ end\\"}]}
]
```

```json golden:flatten-fail-cases
[
  {"name":"an empty file","raw":""},
  {"name":"only whitespace","raw":" \n "},
  {"name":"a truncated second page","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"abc\"},\"nodes\":[{\"id\":\"c1\",\"createdAt\":\"2026-11-10T10:02:00Z\",\"isMinimized\":false,\"lastEditedAt\":null,\"author\":{\"__typename\":\"User\",\"login\":\"strvmarv\"},\"editor\":null,\"body\":\"x\"}]}}}}}\n{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"has"},
  {"name":"a page with errors","raw":"{\"errors\":[{\"type\":\"RATE_LIMITED\",\"message\":\"API rate limit exceeded\"}],\"data\":null}"},
  {"name":"a page with errors alongside data","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[{\"id\":\"c1\",\"createdAt\":\"2026-11-10T10:01:00Z\",\"isMinimized\":false,\"lastEditedAt\":null,\"author\":{\"__typename\":\"User\",\"login\":\"strvmarv\"},\"editor\":null,\"body\":\"x\"}]}}}},\"errors\":[{\"message\":\"partial\"}]}"},
  {"name":"a page with issue null","raw":"{\"data\":{\"repository\":{\"issue\":null}}}"},
  {"name":"a page with repository null","raw":"{\"data\":{\"repository\":null}}"},
  {"name":"a page with no comments nodes","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}}}"},
  {"name":"a last page with hasNextPage true","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"abc\"},\"nodes\":[{\"id\":\"c1\",\"createdAt\":\"2026-11-10T10:04:00Z\",\"isMinimized\":false,\"lastEditedAt\":null,\"author\":{\"__typename\":\"User\",\"login\":\"strvmarv\"},\"editor\":null,\"body\":\"x\"}]}}}}}"},
  {"name":"a last page with no pageInfo","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"nodes\":[]}}}}}"},
  {"name":"trailing text after the last page","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[{\"id\":\"c1\",\"createdAt\":\"2026-11-10T10:01:00Z\",\"isMinimized\":false,\"lastEditedAt\":null,\"author\":{\"__typename\":\"User\",\"login\":\"strvmarv\"},\"editor\":null,\"body\":\"x\"}]}}}}} trailing"},
  {"name":"a non-string body","raw":"{\"data\":{\"repository\":{\"issue\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[{\"id\":\"c1\",\"createdAt\":\"2026-11-10T10:05:00Z\",\"isMinimized\":false,\"lastEditedAt\":null,\"author\":{\"__typename\":\"User\",\"login\":\"strvmarv\"},\"editor\":null,\"body\":null}]}}}}}"}
]
```

```json golden:fetch-commands
[
  {"name":"issue","args":["api","repos/<owner>/<repo>/issues/<n>"]},
  {"name":"pr-create","args":["api","repos/<owner>/<repo>/pulls","-F","title=@<titleFile>","-F","body=@<bodyFile>","-f","head=<branch>","-f","base=<base>"]},
  {"name":"pr-edit","args":["api","-X","PATCH","repos/<owner>/<repo>/pulls/<n>","-F","title=@<titleFile>","-F","body=@<bodyFile>"]},
  {"name":"permission","args":["api","repos/<owner>/<repo>/collaborators/<login>/permission"]},
  {"name":"comment-body","args":["api","repos/<owner>/<repo>/issues/comments/<id>","--jq",".body"]},
  {"name":"comment-post","args":["api","repos/<owner>/<repo>/issues/<n>/comments","-F","body=@<commentFile>"]},
  {"name":"comment-edit","args":["api","-X","PATCH","repos/<owner>/<repo>/issues/comments/<id>","-F","body=@<commentFile>"]},
  {"name":"comments","args":["api","graphql","--paginate","-F","query=@<skill>/queries/comments.graphql","-f","owner=<owner>","-f","name=<repo>","-F","number=<n>"]},
  {"name":"edits","args":["api","graphql","--paginate","-F","query=@<skill>/queries/edits.graphql","-f","owner=<owner>","-f","name=<repo>","-F","number=<n>"]},
  {"name":"renames","args":["api","graphql","--paginate","-F","query=@<skill>/queries/renames.graphql","-f","owner=<owner>","-f","name=<repo>","-F","number=<n>"]}
]
```

```json golden:pages-cases
[
  {"name":"one page","raw":"{\"data\":{\"a\":1}}","expectPages":[{"data":{"a":1}}]},
  {"name":"two pages back to back with no separator","raw":"{\"data\":{\"a\":1}}{\"data\":{\"a\":2}}","expectPages":[{"data":{"a":1}},{"data":{"a":2}}]},
  {"name":"two pages with newlines between","raw":"{\"data\":{\"a\":1}}\n{\"data\":{\"a\":2}}\n","expectPages":[{"data":{"a":1}},{"data":{"a":2}}]},
  {"name":"pretty-printed pages","raw":"{\n  \"data\": {\n    \"a\": [\n      1,\n      {\n        \"b\": 2\n      }\n    ]\n  }\n}\n{\n  \"data\": {\n    \"a\": []\n  }\n}\n","expectPages":[{"data":{"a":[1,{"b":2}]}},{"data":{"a":[]}}]},
  {"name":"an escaped quote before a brace","raw":"{\"body\":\"5\\\" pipe }\"}{\"n\":2}","expectPages":[{"body":"5\" pipe }"},{"n":2}]},
  {"name":"an escaped backslash at the end of a string","raw":"{\"body\":\"C:\\\\\"}{\"n\":2}","expectPages":[{"body":"C:\\"},{"n":2}]},
  {"name":"a quoted brace and a quoted opening brace","raw":"{\"body\":\"a \\\"{\\\" b { c\"}\n{\"n\":2}","expectPages":[{"body":"a \"{\" b { c"},{"n":2}]},
  {"name":"an escaped backslash followed by an escaped quote","raw":"{\"body\":\"\\\\\\\"}\"}{\"n\":2}","expectPages":[{"body":"\\\"}"},{"n":2}]},
  {"name":"trailing whitespace and CRLF","raw":"{\"data\":{\"a\":1}}\r\n  \r\n","expectPages":[{"data":{"a":1}}]},
  {"name":"non-ASCII text","raw":"{\"body\":\"café — 日本語 ✓\"}","expectPages":[{"body":"café — 日本語 ✓"}]},
  {"name":"an empty file","raw":"","expectError":true},
  {"name":"only whitespace","raw":"  \n","expectError":true},
  {"name":"a truncated object","raw":"{\"data\":{\"a\":1}","expectError":true},
  {"name":"a truncated second page","raw":"{\"data\":{\"a\":1}}\n{\"data\":{\"a\":","expectError":true},
  {"name":"trailing text after a page","raw":"{\"data\":{\"a\":1}} junk","expectError":true},
  {"name":"a stray closing brace","raw":"{\"data\":{\"a\":1}}}","expectError":true},
  {"name":"text that is not an object","raw":"[1,2]","expectError":true},
  {"name":"an unterminated string","raw":"{\"a\":\"b","expectError":true}
]
```

```json golden:history-cases
[
  {"name":"a revision matches with the current title","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"Fix login","body":"new body"},"pinned":{"title":"Fix login","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"Fix login","body":"old body"}},
  {"name":"a match that needs the title before a rename","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-05T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"createdAt":"2026-03-03T00:00:00Z","previousTitle":"Fix login","currentTitle":"Fix login v2"}]}}}}}],"current":{"title":"Fix login v2","body":"new body"},"pinned":{"title":"Fix login","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"Fix login","body":"old body"}},
  {"name":"a title saved after the body in a separate save matches through the title chain","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-02T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T10:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"createdAt":"2026-03-01T10:00:05Z","previousTitle":"Draft","currentTitle":"Final"},{"createdAt":"2026-03-02T01:00:00Z","previousTitle":"Final","currentTitle":"Later"}]}}}}}],"current":{"title":"Later","body":"new body"},"pinned":{"title":"Final","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T10:00:00Z","title":"Final","body":"old body"}},
  {"name":"a rename-only change on a never-edited issue (edits connection null) is found as current under the earlier title","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":null}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"createdAt":"2026-03-02T00:00:00Z","previousTitle":"Old title","currentTitle":"New title"}]}}}}}],"current":{"title":"New title","body":"the body"},"pinned":{"title":"Old title","body":"the body"},"expect":{"found":true,"source":"current","editedAt":null,"title":"Old title","body":"the body"}},
  {"name":"a never-edited issue returns an empty non-null edits connection and the current text matches","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"totalCount":0,"nodes":[]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"Same","body":"the body"},"pinned":{"title":"Same","body":"the body"},"expect":{"found":true,"source":"current","editedAt":null,"title":"Same","body":"the body"}},
  {"name":"a never-edited issue with an empty non-null connection and no match","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"totalCount":0,"nodes":[]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"Same","body":"the body"},"pinned":{"title":"Same","body":"another body"},"expect":{"found":false}},
  {"name":"an empty non-null connection that still carries pageInfo","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"Same","body":"the body"},"pinned":{"title":"Same","body":"the body"},"expect":{"found":true,"source":"current","editedAt":null,"title":"Same","body":"the body"}},
  {"name":"a null timelineItems connection is an empty rename list","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":null}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"T","body":"old body"}},
  {"name":"a revision with a null diff is skipped, an older one matches","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-02T00:00:00Z","deletedAt":null,"diff":null,"editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"T","body":"old body"}},
  {"name":"a deleted revision is skipped even when its text would match","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-02T00:00:00Z","deletedAt":"2026-03-04T00:00:00Z","diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"old body"},"expect":{"found":false}},
  {"name":"a null current body is the empty string","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":null}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":null},"pinned":{"title":"T","body":""},"expect":{"found":true,"source":"current","editedAt":null,"title":"T","body":""}},
  {"name":"a pinned hash that is not 64 hex is found:false, not an error","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"old body"},"pinnedBodyHash":"not-a-hash","expect":{"found":false}},
  {"name":"an empty pinned hash is found:false","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"old body"},"pinnedBodyHash":"","expect":{"found":false}},
  {"name":"two renames out of order in the page are sorted first","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-09T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"createdAt":"2026-03-06T00:00:00Z","previousTitle":"B","currentTitle":"C"},{"createdAt":"2026-03-03T00:00:00Z","previousTitle":"A","currentTitle":"B"}]}}}}}],"current":{"title":"C","body":"new body"},"pinned":{"title":"A","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"A","body":"old body"}},
  {"name":"no revision matches","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"older body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"a body that was deleted from the history"},"expect":{"found":false}},
  {"name":"a match on the second page of edits","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":true,"endCursor":"Y3Vyc29yOjI="},"nodes":[{"editedAt":"2026-03-05T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-04T00:00:00Z","deletedAt":null,"diff":"mid body","editor":{"login":"strvmarv"}}]}}}}},{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":"Y3Vyc29yOjM="},"nodes":[{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"old body"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"T","body":"old body"}},
  {"name":"the current body matches before any revision is tried","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old body","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"new body"},"expect":{"found":true,"source":"current","editedAt":null,"title":"T","body":"new body"}},
  {"name":"a revision with CRLF line endings matches the LF-hashed pin","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"editedAt":"2026-03-03T00:00:00Z","deletedAt":null,"diff":"new body","editor":{"login":"strvmarv"}},{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"line one\r\nline two  \r\n","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"new body"},"pinned":{"title":"T","body":"line one\nline two"},"expect":{"found":true,"source":"revision","editedAt":"2026-03-01T00:00:00Z","title":"T","body":"line one\r\nline two  \r\n"}},
  {"name":"an edits page with errors (error)","editsPages":[{"errors":[{"message":"boom"}],"data":null}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"b"},"pinned":{"title":"T","body":"b"},"expect":{"error":true}},
  {"name":"a rename page with errors (error)","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"renamePages":[{"errors":[{"message":"boom"}],"data":{"repository":{"issue":{"timelineItems":null}}}}],"current":{"title":"T","body":"b"},"pinned":{"title":"T","body":"b"},"expect":{"error":true}},
  {"name":"a final edits page with hasNextPage true (error)","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":true,"endCursor":"abc"},"nodes":[{"editedAt":"2026-03-01T00:00:00Z","deletedAt":null,"diff":"old","editor":{"login":"strvmarv"}}]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"b"},"pinned":{"title":"T","body":"b"},"expect":{"error":true}},
  {"name":"a final rename page with hasNextPage true (error)","editsPages":[{"data":{"repository":{"issue":{"userContentEdits":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":true,"endCursor":"abc"},"nodes":[{"createdAt":"2026-03-01T00:00:00Z","previousTitle":"A","currentTitle":"B"}]}}}}}],"current":{"title":"T","body":"b"},"pinned":{"title":"T","body":"b"},"expect":{"error":true}},
  {"name":"a missing issue (error)","editsPages":[{"data":{"repository":{"issue":null}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"b"},"pinned":{"title":"T","body":"b"},"expect":{"error":true}},
  {"name":"a page with no connection member (error)","editsPages":[{"data":{"repository":{"issue":{}}}}],"renamePages":[{"data":{"repository":{"issue":{"timelineItems":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}],"current":{"title":"T","body":"b"},"pinned":{"title":"T","body":"b"},"expect":{"error":true}}
]
```

```json golden:write-access-cases
[
  {"name":"an admin permission object","kind":"result","text":"{\"permission\":\"admin\",\"role_name\":\"admin\",\"user\":{\"login\":\"strvmarv\"}}","httpStatus":null,"expect":{"writeAccess":true}},
  {"name":"a write permission object","kind":"result","text":"{\"permission\":\"write\",\"role_name\":\"write\"}","httpStatus":200,"expect":{"writeAccess":true}},
  {"name":"a read permission object","kind":"result","text":"{\"permission\":\"read\",\"role_name\":\"read\"}","httpStatus":null,"expect":{"writeAccess":false}},
  {"name":"an absent file with httpStatus 404 is no write access, not an error","kind":"result","text":null,"httpStatus":404,"expect":{"writeAccess":false}},
  {"name":"an absent file with 403 is unavailable","kind":"result","text":null,"httpStatus":403,"expect":{"error":"unavailable"}},
  {"name":"an absent file with 500 is unavailable","kind":"result","text":null,"httpStatus":500,"expect":{"error":"unavailable"}},
  {"name":"an absent file with no status is unavailable","kind":"result","text":null,"httpStatus":null,"expect":{"error":"unavailable"}},
  {"name":"unparseable text with no status is an error","kind":"result","text":"not json {","httpStatus":null,"expect":{"error":"unparseable"}},
  {"name":"JSON that is not a permission object is an error","kind":"result","text":"{\"message\":\"Not Found\"}","httpStatus":null,"expect":{"error":"unparseable"}},
  {"name":"a JSON array is an error","kind":"result","text":"[]","httpStatus":null,"expect":{"error":"unparseable"}},
  {"name":"a manifest of two logins, one 404, assembles the map","kind":"manifest","existing":null,"entries":[{"login":"a","text":"{\"permission\":\"write\",\"role_name\":\"write\",\"user\":{\"login\":\"a\",\"id\":1}}","httpStatus":null},{"login":"b","text":null,"httpStatus":404}],"expectMap":{"a":{"permission":"write","role_name":"write"},"b":null}},
  {"name":"an empty manifest with no existing file writes an empty map","kind":"manifest","existing":null,"entries":[],"expectMap":{}},
  {"name":"a later pass merges: existing logins kept, an entry overrides the same login","kind":"manifest","existing":{"a":{"permission":"read","role_name":"read"},"c":{"permission":"admin","role_name":"admin"}},"entries":[{"login":"a","text":"{\"permission\":\"write\",\"role_name\":\"write\"}","httpStatus":null},{"login":"constructor","text":null,"httpStatus":404}],"expectMap":{"a":{"permission":"write","role_name":"write"},"c":{"permission":"admin","role_name":"admin"},"constructor":null}},
  {"name":"one unavailable entry fails the whole manifest and writes nothing","kind":"manifest","existing":null,"entries":[{"login":"a","text":"{\"permission\":\"write\",\"role_name\":\"write\"}","httpStatus":null},{"login":"b","text":null,"httpStatus":403}],"expectError":"unavailable"},
  {"name":"a hostile login in a manifest is rejected","kind":"manifest","existing":null,"entries":[{"login":"x; rm -rf /","text":"{\"permission\":\"write\",\"role_name\":\"write\"}","httpStatus":null}],"expectError":"bad login"}
]
```

```json golden:select-cases
[
  {"name":"a definer in the map and a kickoff by an unknown login E: E is needed and the pass is incomplete","comments":[{"id":"IC_def","createdAt":"2026-11-10T10:01:00Z","authorLogin":"jane-doe_acme","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small bodyHash=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\n```\n"},{"id":"IC_kick","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"jane-doe_acme":{"role_name":"write","permission":"write"}},"expect":{"complete":false,"needPermissions":["strvmarv"],"counted":["IC_def"],"checkpoints":[{"id":"IC_def","phase":"defined","createdAt":"2026-11-10T10:01:00Z"}],"latest":{"id":"IC_def","phase":"defined","createdAt":"2026-11-10T10:01:00Z","fields":{"track":"small","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}},"pin":{"id":"IC_def","phase":"defined","fields":{"track":"small","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}},"progress":null}},
  {"name":"both logins in the map: complete, the kickoff is progress and the latest","comments":[{"id":"IC_def","createdAt":"2026-11-10T10:01:00Z","authorLogin":"jane-doe_acme","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for defined.\n\n```\ntr-develop/v1 phase=defined branch=feat/x track=small bodyHash=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\n```\n"},{"id":"IC_kick","createdAt":"2026-11-10T10:02:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"jane-doe_acme":{"role_name":"write","permission":"write"},"strvmarv":{"role_name":"admin"}},"expect":{"complete":true,"needPermissions":[],"counted":["IC_def","IC_kick"],"checkpoints":[{"id":"IC_def","phase":"defined","createdAt":"2026-11-10T10:01:00Z"},{"id":"IC_kick","phase":"kickoff","createdAt":"2026-11-10T10:02:00Z"}],"latest":{"id":"IC_kick","phase":"kickoff","createdAt":"2026-11-10T10:02:00Z","fields":{"track":"small","branch":"feat/x"}},"pin":{"id":"IC_def","phase":"defined","fields":{"track":"small","branch":"feat/x","bodyHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}},"progress":{"id":"IC_kick","phase":"kickoff","fields":{"track":"small","branch":"feat/x"}}}},
  {"name":"a Bot author's checkpoint comment is not counted and its login is not looked up","comments":[{"id":"IC_bot","createdAt":"2026-11-10T10:21:00Z","authorLogin":"strvmarv","authorType":"Bot","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{},"expect":{"complete":true,"needPermissions":[],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"a login missing from the permissions map is listed and the pass is incomplete","comments":[{"id":"IC_k","createdAt":"2026-11-10T10:31:00Z","authorLogin":"newcomer","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{},"expect":{"complete":false,"needPermissions":["newcomer"],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"a missing permissions file reads as an empty map","comments":[{"id":"IC_k","createdAt":"2026-11-10T10:31:00Z","authorLogin":"newcomer","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":null,"expect":{"complete":false,"needPermissions":["newcomer"],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"a login whose entry is null is not listed and its comment does not count","comments":[{"id":"IC_k","createdAt":"2026-11-10T10:31:00Z","authorLogin":"newcomer","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"newcomer":null},"expect":{"complete":true,"needPermissions":[],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"a login whose entry is a read-only permission is not listed and does not count","comments":[{"id":"IC_k","createdAt":"2026-11-10T10:31:00Z","authorLogin":"newcomer","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"newcomer":{"role_name":"read","permission":"read"}},"expect":{"complete":true,"needPermissions":[],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"hostile logins are never listed and their comments do not count","comments":[{"id":"IC_h1","createdAt":"2026-11-10T10:41:00Z","authorLogin":"$(id)","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"IC_h2","createdAt":"2026-11-10T10:42:00Z","authorLogin":"x; rm -rf /","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"$(id)":{"role_name":"admin"}},"expect":{"complete":true,"needPermissions":[],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"the valid logins constructor and toString are listed when absent","comments":[{"id":"IC_c1","createdAt":"2026-11-10T10:51:00Z","authorLogin":"constructor","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"IC_c2","createdAt":"2026-11-10T10:52:00Z","authorLogin":"toString","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for tasks.\n\n```\ntr-develop/v1 phase=tasks branch=feat/x track=small\n```\n"}],"permissions":{},"expect":{"complete":false,"needPermissions":["constructor","toString"],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"the valid logins constructor and toString count when present and writers","comments":[{"id":"IC_c1","createdAt":"2026-11-10T10:51:00Z","authorLogin":"constructor","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"IC_c2","createdAt":"2026-11-10T10:52:00Z","authorLogin":"toString","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for tasks.\n\n```\ntr-develop/v1 phase=tasks branch=feat/x track=small\n```\n"}],"permissions":{"constructor":{"role_name":"write"},"toString":{"permission":"write"}},"expect":{"complete":true,"needPermissions":[],"counted":["IC_c1","IC_c2"],"checkpoints":[{"id":"IC_c1","phase":"kickoff","createdAt":"2026-11-10T10:51:00Z"},{"id":"IC_c2","phase":"tasks","createdAt":"2026-11-10T10:52:00Z"}],"latest":{"id":"IC_c2","phase":"tasks","createdAt":"2026-11-10T10:52:00Z","fields":{"track":"small","branch":"feat/x"}},"pin":null,"progress":{"id":"IC_c2","phase":"tasks","fields":{"track":"small","branch":"feat/x"}}}},
  {"name":"an unknown User editor of a checkpoint comment is listed","comments":[{"id":"IC_k","createdAt":"2026-11-10T10:61:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"jane-doe_acme","editorType":"User","lastEditedAt":"2026-11-10T10:59:00Z","isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"strvmarv":{"role_name":"write"}},"expect":{"complete":false,"needPermissions":["jane-doe_acme"],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"a Bot editor's login is never looked up and the comment does not count","comments":[{"id":"IC_k2","createdAt":"2026-11-10T10:62:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":"robo","editorType":"Bot","lastEditedAt":"2026-11-10T10:59:00Z","isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"strvmarv":{"role_name":"write"}},"expect":{"complete":true,"needPermissions":[],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"latest is the latest counted checkpoint of any phase (blocked included), progress is not","comments":[{"id":"IC_k","createdAt":"2026-11-10T10:71:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"IC_b","createdAt":"2026-11-10T10:72:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for blocked.\n\n```\ntr-develop/v1 phase=blocked branch=feat/x stage=build blocker=3\n```\n"}],"permissions":{"strvmarv":{"role_name":"write"}},"expect":{"complete":true,"needPermissions":[],"counted":["IC_k","IC_b"],"checkpoints":[{"id":"IC_k","phase":"kickoff","createdAt":"2026-11-10T10:71:00Z"},{"id":"IC_b","phase":"blocked","createdAt":"2026-11-10T10:72:00Z"}],"latest":{"id":"IC_b","phase":"blocked","createdAt":"2026-11-10T10:72:00Z","fields":{"branch":"feat/x","stage":"build","blocker":"3"}},"pin":null,"progress":{"id":"IC_k","phase":"kickoff","fields":{"track":"small","branch":"feat/x"}}}},
  {"name":"ordinary commenters and invalid multi-line comments are never looked up; a minimized one by a known writer is skipped","comments":[{"id":"IC_chat","createdAt":"2026-11-10T10:81:00Z","authorLogin":"passerby","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Thanks for looking at this."},{"id":"IC_multi","createdAt":"2026-11-10T10:82:00Z","authorLogin":"quoter","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n"},{"id":"IC_min","createdAt":"2026-11-10T10:83:00Z","authorLogin":"strvmarv","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":true,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"}],"permissions":{"strvmarv":{"role_name":"write"}},"expect":{"complete":true,"needPermissions":[],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}},
  {"name":"a login that wrote two checkpoints is listed once","comments":[{"id":"IC_dup","createdAt":"2026-11-10T10:91:00Z","authorLogin":"newcomer","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for kickoff.\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n```\n"},{"id":"IC_dup2","createdAt":"2026-11-10T10:92:00Z","authorLogin":"newcomer","authorType":"User","editorLogin":null,"editorType":null,"lastEditedAt":null,"isMinimized":false,"body":"Summary for tasks.\n\n```\ntr-develop/v1 phase=tasks branch=feat/x track=small\n```\n"}],"permissions":{},"expect":{"complete":false,"needPermissions":["newcomer"],"counted":[],"checkpoints":[],"latest":null,"pin":null,"progress":null}}
]
```
