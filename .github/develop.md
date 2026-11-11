# develop profile

This file tells the `develop` skill how to work in this repository: which GitHub account to use, whether the
ticket standard applies, and the commands to run before a pull request opens. The skill reads the first
`text` block that starts with the header line; the format is described in `skills/develop/profile.md`.

```text
tr-develop/profile-v1
ticket.standard: on
gh.account: strvmarv
```

## Verify

The .NET suite, without the integration category, then the shim suites, then the catalog drift guard. The second
command lists a glob for every directory that `npm run test:shim` lists. The globs are unquoted so that `sh` expands them, which
works on every Node version (a quoted glob handed to `node --test` needs Node 21 or newer, and CI runs Node 20).

```sh verify
dotnet test src/TotalRecall.sln --filter "Category!=Integration"
```

```sh verify
node --test bin/shim/*.test.js pi/*.test.js codex/*.test.js skills/develop/scripts/*.test.js copilot/*.test.js cursor/*.test.js scripts/fetch-binary.test.js hooks/hooks.config.test.js
```

The catalog drift guard that CI runs after the build: `catalog.json` must equal what `dump-catalog` prints, so a
change to the MCP tools has to regenerate it. CI builds first and runs with `--no-build`, because otherwise MSBuild
output leaks into the dumped catalog. The block does the same, and diffs the output on stdout instead of writing a
file, so it leaves no file outside the worktree (`dump-catalog` uses a throwaway temp directory that it deletes).

```sh verify
dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q
dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -
```
