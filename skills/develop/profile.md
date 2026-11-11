# The repository profile

The format reference for `.github/develop.md` (spec section 15). The parser in `scripts/profile.js`
reads the golden below; Task 18 completes this file.

## Format

```json golden:profile-cases
[
  {
    "name": "full example with gh.account and two verify blocks",
    "markdown": "```text\ntr-develop/profile-v1\nbase: main\nticket.standard: on\nticket.template.feature: feature_request.md\nticket.template.bug: bug_report.yml\ngh.account: strvmarv\n```\n\n## Verify\n\n```sh verify\ndotnet test src/TotalRecall.sln --filter \"Category!=Integration\"\n```\n\n```sh verify\nnpm run test:shim\n```\n",
    "expect": {
      "ok": true,
      "profile": {
        "base": "main",
        "ticketStandard": "on",
        "templates": { "feature": "feature_request.md", "bug": "bug_report.yml" },
        "ghAccount": "strvmarv",
        "verify": ["dotnet test src/TotalRecall.sln --filter \"Category!=Integration\"", "npm run test:shim"]
      }
    }
  },
  {
    "name": "the same full example with CRLF line endings",
    "markdown": "```text\r\ntr-develop/profile-v1\r\nbase: main\r\nticket.standard: on\r\nticket.template.feature: feature_request.md\r\nticket.template.bug: bug_report.yml\r\ngh.account: strvmarv\r\n```\r\n\r\n## Verify\r\n\r\n```sh verify\r\ndotnet test src/TotalRecall.sln --filter \"Category!=Integration\"\r\n```\r\n\r\n```sh verify\r\nnpm run test:shim\r\n```\r\n",
    "expect": {
      "ok": true,
      "profile": {
        "base": "main",
        "ticketStandard": "on",
        "templates": { "feature": "feature_request.md", "bug": "bug_report.yml" },
        "ghAccount": "strvmarv",
        "verify": ["dotnet test src/TotalRecall.sln --filter \"Category!=Integration\"", "npm run test:shim"]
      }
    }
  },
  {
    "name": "minimal block: the header only, every default applies",
    "markdown": "```text\ntr-develop/profile-v1\n```\n",
    "expect": {
      "ok": true,
      "profile": { "base": null, "ticketStandard": "on", "templates": {}, "ghAccount": null, "verify": [] }
    }
  },
  {
    "name": "an unknown key is ignored",
    "markdown": "```text\ntr-develop/profile-v1\nfuture.switch: anything at all\nbase: release/1.x\n```\n",
    "expect": {
      "ok": true,
      "profile": { "base": "release/1.x", "ticketStandard": "on", "templates": {}, "ghAccount": null, "verify": [] }
    }
  },
  {
    "name": "ticket.standard must be on or off",
    "markdown": "```text\ntr-develop/profile-v1\nticket.standard: maybe\n```\n",
    "expect": { "ok": false, "error": "bad-value:ticket.standard" }
  },
  {
    "name": "gh.account must be a login, not a shell fragment",
    "markdown": "```text\ntr-develop/profile-v1\ngh.account: x; rm\n```\n",
    "expect": { "ok": false, "error": "bad-value:gh.account" }
  },
  {
    "name": "base must not start with a dash",
    "markdown": "```text\ntr-develop/profile-v1\nbase: -x\n```\n",
    "expect": { "ok": false, "error": "bad-value:base" }
  },
  {
    "name": "a template name must not climb out of the template folder",
    "markdown": "```text\ntr-develop/profile-v1\nticket.template.bug: ../../etc/passwd\n```\n",
    "expect": { "ok": false, "error": "bad-value:ticket.template.bug" }
  },
  {
    "name": "a repeated known key is rejected, not overridden",
    "markdown": "```text\ntr-develop/profile-v1\nbase: main\nbase: dev\n```\n",
    "expect": { "ok": false, "error": "bad-value:base" }
  },
  {
    "name": "an empty sh verify block is rejected",
    "markdown": "```text\ntr-develop/profile-v1\n```\n\n```sh verify\n\n```\n",
    "expect": { "ok": false, "error": "bad-value:verify" }
  },
  {
    "name": "a document with no profile block",
    "markdown": "# Repository\n\nNo profile here.\n\n```text\nnot a profile\n```\n",
    "expect": { "ok": false, "error": "no-profile" }
  }
]
```
