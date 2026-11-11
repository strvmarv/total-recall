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

