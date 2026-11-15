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

Two setup steps, then the .NET suite without the integration category, then the shim suites, then the catalog drift guard.
A fresh worktree has neither `node_modules/` nor the embedding model, so the first two blocks do what CI does before it builds.
The fourth command is `npm run test:shim`, which runs `scripts/run-shim-tests.js`: the one list of the Node test files (`SHIM_TEST_TARGETS`),
handed to `node --test` as explicit files so that it works on Node 20 and later (a directory argument fails on Node 24, a quoted glob
needs Node 21 or newer, and CI runs Node 20).

```sh verify
npm ci --ignore-scripts
```

`npm ci` installs `sqlite-vec`, whose per-platform native library arrives as an optional dependency and which the Infrastructure
project copies from `node_modules/` into the test output (the comment on the "Install npm dependencies" step of `dotnet-ci.yml`
explains it). `--ignore-scripts` is deliberate: this package's `postinstall` rewrites other tools' configuration under the home
directory and downloads the engine, and its `prepare` writes the shared `.git/config`, none of which a test run needs or may do.
With it, the command writes only inside the worktree, plus npm's own cache.

```sh verify
[ -f models/bge-small-en-v1.5/model.onnx ] && [ -f models/bge-small-en-v1.5/tokenizer.json ] || sh scripts/fetch-bge-small.sh
```

The tests load the bge-small model, which is git-ignored and fetched, as CI does with the same script. The script downloads 133 MB
and checks its sha256, so every fresh worktree downloads it once. It writes into `models/bge-small-en-v1.5/` inside the worktree. A
worktree that is reused, as a resumed run does, already has both files, and the guard then skips the download. If a download ever
fails its checksum, delete `models/bge-small-en-v1.5/` so that the next run fetches it again.

```sh verify
dotnet test src/TotalRecall.sln --filter "Category!=Integration"
```

```sh verify
npm run test:shim
```

The catalog drift guard that CI runs after the build: `catalog.json` must equal what `dump-catalog` prints, so a
change to the MCP tools has to regenerate it. CI builds first and runs with `--no-build`, because otherwise MSBuild
output leaks into the dumped catalog. The block does the same, and diffs the output on stdout instead of writing a
file, so it leaves no file outside the worktree (`dump-catalog` uses a throwaway temp directory that it deletes).

```sh verify
dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q
dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -
```
