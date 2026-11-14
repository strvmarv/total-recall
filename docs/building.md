# Building from source

## Prerequisites

These apply only if you're building from source. The prebuilt binary is self-contained — no .NET runtime, no system SQLite, no Bun required.

- **.NET 10 SDK** — pinned by `global.json` at the repo root; builds the `net10.0` NativeAOT target
- **npm** — for `npm ci`, which pulls `sqlite-vec` native libs needed by the csproj copy targets
- **Embedding model** — run `sh scripts/fetch-bge-small.sh` once to fetch + sha256-verify the `bge-small-en-v1.5` ONNX model (~133 MB) into `models/bge-small-en-v1.5/`. The model is no longer committed to the repo (not in Git LFS); release builds fetch and bundle it into the per-RID artifact.

## Build

```bash
git clone https://github.com/strvmarv/total-recall.git
cd total-recall
sh scripts/fetch-bge-small.sh              # fetch + sha256-verify the ONNX model (~133 MB)
npm ci                                     # pulls sqlite-vec native libs into node_modules/
dotnet build src/TotalRecall.sln
dotnet test src/TotalRecall.sln --filter "Category!=Integration"
dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r win-x64 -p:PublishAot=true
# (swap win-x64 for your RID: linux-x64, linux-arm64, osx-arm64)
```

The publish output lands in `src/TotalRecall.Host/bin/Release/net10.0/<rid>/publish/` with the binary plus all sibling native libs (`libonnxruntime.*`, `libe_sqlite3.*`, `runtimes/vec0.*`) ready to run.

Supported RIDs: `linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`. Intel Mac (`osx-x64`) is not shipped.
