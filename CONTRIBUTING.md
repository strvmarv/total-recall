# Contributing to total-recall

total-recall is a **.NET 8 NativeAOT** plugin since 0.8.0 — a C# imperative shell + F# functional core, distributed as prebuilt per-platform binaries via npm with a thin Node launcher (`bin/start.js`). The TypeScript implementation that lived in `src/` through 0.7.x was stripped during the 0.7.2 → 0.8.0 cutover (see `CHANGELOG.md` for the strip history). This guide describes the current contributor workflow against the .NET tree.

---

## Getting Started

### Prerequisites

- **.NET 10 SDK** — pinned by `global.json` at the repo root (`{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`). The .NET 10 SDK builds the `net8.0` target framework cleanly. Install from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).
- **Node.js >= 20** — used only by `npm install` to pull the per-platform `sqlite-vec` native extension into `node_modules/`. The Infrastructure csproj's `<Content Include="node_modules/sqlite-vec-<rid>/vec0.*">` step copies the matching variant into the build output.
- **Git LFS** — `git lfs install` before cloning. The `all-MiniLM-L6-v2` ONNX model is stored in LFS.

### Clone and build

```bash
git clone https://github.com/strvmarv/total-recall.git
cd total-recall
npm install                                          # pulls sqlite-vec native libs
dotnet restore src/TotalRecall.sln
dotnet build src/TotalRecall.sln --configuration Release
dotnet test src/TotalRecall.sln --no-build --configuration Release
```

Expected: build succeeds with **0 warnings, 0 errors**; tests show **944 passing** across:

- `TotalRecall.Core.Tests` (F#, Expecto): tokenizer, decay, ranking, parsers — ~59 tests
- `TotalRecall.Cli.Tests` (xUnit): CLI commands — ~150 tests
- `TotalRecall.Server.Tests` (xUnit): MCP handlers, AutoMigrationGuard, lifecycle — ~324 tests
- `TotalRecall.Infrastructure.Tests` (xUnit): storage, embedding, importers, ingestion, eval — ~411 tests

### Local AOT publish (optional, for end-to-end testing)

```bash
dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj \
  -c Release -r linux-x64 -p:PublishAot=true
# (swap linux-x64 for linux-arm64, osx-arm64, or win-x64)
```

The published binary lives at `src/TotalRecall.Host/bin/Release/net8.0/<rid>/publish/total-recall` (or `total-recall.exe` on Windows). Smoke test:

```bash
SCRATCH=$(mktemp -d)
TOTAL_RECALL_DB_PATH="$SCRATCH/test.db" \
  src/TotalRecall.Host/bin/Release/net8.0/linux-x64/publish/total-recall status
rm -rf "$SCRATCH"
```

Expected output: tier counts, KB info, embedding model `all-MiniLM-L6-v2`, schema version 5+, exit 0.

The new `VerifyVecExtensionPublished` MSBuild target (added in 0.8.0-beta.6) runs after `dotnet publish` and emits an `<Error>` if `runtimes/vec0.{so,dylib,dll}` is missing from the publish output. If you ever see that error, run `npm ci` at the repo root and verify `node_modules/sqlite-vec-<rid>/vec0.*` exists for your target RID.

---

## Project layout

```
src/
├── TotalRecall.Core/             — F# functional core: pure functions, no I/O
│   ├── Types.fs                  — Tier, ContentType, Entry DUs (illegal states unrepresentable)
│   ├── Tokenizer.fs              — canonical BERT BasicTokenization + WordPiece
│   ├── Decay.fs                  — half-life-based decay scoring
│   ├── Ranking.fs                — hybrid vector + FTS score combination
│   ├── Parsers.fs                — code/markdown chunking parsers (regex, no AST)
│   ├── Chunker.fs                — chunk dispatch + overlap/budget enforcement
│   └── Config.fs                 — config record types + validation
│
├── TotalRecall.Infrastructure/   — C# imperative shell: I/O, framework dependencies
│   ├── Storage/                  — Microsoft.Data.Sqlite + sqlite-vec, Schema.cs (migrations 1..5)
│   ├── Embedding/                — Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers
│   ├── Importers/                — ClaudeCode, CopilotCli, Cursor, Cline, OpenCode, Hermes, ProjectDocs
│   ├── Ingestion/                — file ingester, hierarchical index, chunking dispatch
│   ├── Search/                   — vector search, FTS search, hybrid ranking
│   ├── Telemetry/                — retrieval event log, compaction log, import log
│   ├── Eval/                     — benchmark runner, comparison metrics, snapshot store
│   ├── Diagnostics/              — ExceptionLogger.LogChain (walks InnerException chain, AOT-safe)
│   └── Config/                   — TOML loader, defaults.toml as embedded resource
│
├── TotalRecall.Server/           — JSON-RPC stdio MCP server, 32 tool handlers
│   ├── McpServer.cs              — request dispatch, lifecycle, error translation
│   ├── Handlers/                 — one file per MCP tool (memory_*, kb_*, eval_*, session_*, status, …)
│   └── AutoMigrationGuard.cs     — 5-state migration state machine (TS → .NET on first launch)
│
├── TotalRecall.Cli/              — CLI command surface (Spectre.Console rendering)
│   ├── CliApp.cs                 — entry point, --version, command dispatch
│   └── Commands/                 — Status, Migrate, Memory/{Get,Update,…}, Kb/{List,Refresh,…}, Config/{Get,Set}
│
└── TotalRecall.Host/             — composition root, AOT entry point
    ├── Program.cs                — Main(), DI wiring, migration guard, server start
    └── TotalRecall.Host.csproj   — PublishAot=true, runtimes/vec0.* verification target

tests/
├── TotalRecall.Core.Tests/       — Expecto + FsCheck (F# functional core tests)
├── TotalRecall.Cli.Tests/        — xUnit (CLI commands)
├── TotalRecall.Server.Tests/     — xUnit (MCP handlers + AutoMigrationGuard state machine)
└── TotalRecall.Infrastructure.Tests/ — xUnit (storage, embedding, importers, eval)

bin/start.js                      — Node launcher: detect RID, exec binaries/<rid>/total-recall
scripts/fetch-binary.js           — first-launch downloader for git-clone installs
scripts/postinstall.js            — npm postinstall hook (delegates to fetch-binary)
scripts/verify-binaries.js        — prepublishOnly safety check (4 RIDs present)
.github/workflows/dotnet-ci.yml   — push/PR CI: dotnet build + test on ubuntu-latest
.github/workflows/release.yml     — v* tag CI: 4-platform AOT matrix + npm publish + GitHub Release
```

---

## Adding a New Host Tool Importer

Host importers detect a specific tool's presence, scan its memory files, and migrate them into total-recall on first run. They're invoked by `session_start` and deduplicated via `import_log` (content hash → entry id).

### 1. Implement `IImporter`

The interface lives in `src/TotalRecall.Infrastructure/Importers/IImporter.cs`. The contract:

```csharp
public interface IImporter
{
    string Name { get; }
    bool Detect();
    ImportScanResult Scan();
    Task<ImportResult> ImportMemoriesAsync(ISqliteStore store, IEmbedder embedder, string? project, CancellationToken ct);
    Task<ImportResult> ImportKnowledgeAsync(ISqliteStore store, IEmbedder embedder, CancellationToken ct);
}
```

Create `src/TotalRecall.Infrastructure/Importers/MyToolImporter.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Importers;

public sealed class MyToolImporter : IImporter
{
    public string Name => "my-tool";

    private static readonly string MyToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".my-tool");

    public bool Detect() => Directory.Exists(MyToolDir);

    public ImportScanResult Scan()
    {
        if (!Detect()) return new ImportScanResult(0, 0, 0);
        var files = Directory.GetFiles(MyToolDir, "*.md").Length;
        return new ImportScanResult(MemoryFiles: files, KnowledgeFiles: 0, SessionFiles: 0);
    }

    public async Task<ImportResult> ImportMemoriesAsync(
        ISqliteStore store,
        IEmbedder embedder,
        string? project,
        CancellationToken ct)
    {
        var result = new ImportResult();
        if (!Detect()) return result;

        foreach (var file in Directory.GetFiles(MyToolDir, "*.md"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = (await File.ReadAllTextAsync(file, ct)).Trim();
                if (string.IsNullOrEmpty(raw)) { result.Skipped++; continue; }

                var hash = ComputeHash(raw);
                if (store.ImportLogContains(hash)) { result.Skipped++; continue; }

                var id = Guid.NewGuid().ToString();
                var embedding = await embedder.EmbedAsync(raw, ct);

                store.InsertWithEmbedding(new EntryRecord
                {
                    Id = id,
                    Content = raw,
                    Tier = "warm",
                    ContentType = "memory",
                    SourceTool = Name,
                    Source = file,
                    Project = project,
                }, embedding);

                store.ImportLogInsert(new ImportLogEntry(
                    Id: Guid.NewGuid().ToString(),
                    SourceTool: Name,
                    SourcePath: file,
                    ContentHash: hash,
                    EntryId: id,
                    Tier: "warm",
                    ContentType: "memory"));

                result.Imported++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return result;
    }

    public Task<ImportResult> ImportKnowledgeAsync(ISqliteStore store, IEmbedder embedder, CancellationToken ct)
        => Task.FromResult(new ImportResult()); // my-tool has no separate knowledge files

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
```

### 2. Register the importer

Add it to the importer collection wired up in `src/TotalRecall.Host/Program.cs` or wherever `IEnumerable<IImporter>` is composed (search for `ClaudeCodeImporter` to find the registration site — all importers are added as a group).

### 3. Add tests

Create `tests/TotalRecall.Infrastructure.Tests/Importers/MyToolImporterTests.cs` mirroring `ClaudeCodeImporterTests.cs`. Cover at minimum: detect-true / detect-false, empty scan, dedup against existing import_log entries, and at least one round-trip insert with assertion on the resulting entry shape.

---

## Adding a New Content Type

Content types (currently `"memory"` and `"knowledge"`) classify what kind of information an entry holds. Each tier has separate tables per content type (`hot_memories`, `hot_knowledge`, etc.).

### 1. Extend the F# union

In `src/TotalRecall.Core/Types.fs`:

```fsharp
type ContentType =
    | Memory
    | Knowledge
    | MyNewType   // <- add here
```

### 2. Add a schema migration

Schema changes go through the `MigrationRunner` in `src/TotalRecall.Infrastructure/Storage/Schema.cs`. **Do not modify existing migrations** — they're frozen and applied sequentially based on `_schema_version`. Instead, add a new function to the migrations array:

```csharp
// Migration 6: add my-new-type tier tables
private static void Migration6_AddMyNewTypeTables(SqliteConnection conn)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS hot_my_new_type (
            id TEXT PRIMARY KEY,
            content TEXT NOT NULL,
            -- ... mirror the columns from hot_memories
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS hot_my_new_type_vec USING vec0(...);
        -- repeat for warm_my_new_type, cold_my_new_type
    ";
    cmd.ExecuteNonQuery();
}
```

Then register it in the migrations array in lockstep with the version number. The framework runs each migration inside a transaction; partial failures roll back.

### 3. Wire it through the table-name lookup

The canonical table-name lookup in `Schema.cs` (`TableName(Tier, ContentType)`) and the `ContentTableNames[]` array used by `AutoMigrationGuard.InspectDbFormat` for partial-state detection both need updating.

### 4. Add tests

`tests/TotalRecall.Infrastructure.Tests/SchemaTests.cs` for the migration; `tests/TotalRecall.Server.Tests/AutoMigrationGuardTests.cs` for the partial-state detection.

---

## Adding a New Chunking Parser

Chunking lives in `src/TotalRecall.Core/Chunker.fs` (F#, pure). Per-language parsers are in `Parsers.fs`. Adding a new format means extending the parser surface and the dispatch.

### 1. Implement the parser

In `src/TotalRecall.Core/Parsers.fs`:

```fsharp
module Parsers

type Chunk = {
    Content: string
    StartLine: int
    EndLine: int
    HeadingPath: string list option
    SymbolName: string option
    SymbolKind: string option
}

let parseMyFormat (content: string) (maxTokens: int) : Chunk list =
    // Split content into logical units, build Chunk records.
    // Each chunk needs: Content, StartLine, EndLine.
    // Optional: HeadingPath (for outline-like formats), SymbolName/Kind (for code-like formats).
    content.Split("\n---\n", System.StringSplitOptions.None)
    |> Array.toList
    |> List.mapi (fun i text ->
        { Content = text
          StartLine = i + 1
          EndLine = i + text.Split('\n').Length
          HeadingPath = None
          SymbolName = None
          SymbolKind = None })
```

### 2. Register the parser in the chunker dispatch

In `Chunker.fs`, add your file extensions to the dispatch:

```fsharp
let chunk (content: string) (filePath: string) (opts: ChunkOptions) : Chunk list =
    let ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant()
    match ext with
    | ".md" | ".markdown" -> Parsers.parseMarkdown content opts.MaxTokens
    | ".cs" | ".fs" | ".ts" | ".js" | ".py" | ".go" -> Parsers.parseCode content filePath opts.MaxTokens
    | ".myext" | ".myfmt" -> Parsers.parseMyFormat content opts.MaxTokens   // <- add here
    | _ -> Parsers.parseParagraphs content opts.MaxTokens
```

### 3. Add tests

Create `tests/TotalRecall.Core.Tests/ParsersTests.fs` test cases (Expecto). Test at minimum: empty input, single section, multiple sections, sections exceeding `maxTokens`.

---

## Running Tests

Run all tests once:

```bash
dotnet test src/TotalRecall.sln --no-build --configuration Release
```

Run a specific test project:

```bash
dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj
dotnet test tests/TotalRecall.Server.Tests/TotalRecall.Server.Tests.csproj
```

Run a specific test by filter:

```bash
dotnet test src/TotalRecall.sln --filter "FullyQualifiedName~AutoMigrationGuard"
```

For F# tests (Expecto), the project's test runner is invoked the same way — `dotnet test` works uniformly across xUnit (C#) and Expecto (F#) projects.

---

## Running Benchmarks

Once the MCP server is running and connected to your coding assistant, use the eval commands:

```
/total-recall:commands eval                      # Live retrieval metrics for current session
/total-recall:commands eval --benchmark          # Run synthetic benchmark suite
/total-recall:commands eval --snapshot baseline  # Save current config as a named baseline
/total-recall:commands eval --compare baseline   # Compare current config against saved baseline
/total-recall:commands eval --grow               # Add real query misses to benchmark suite
```

Or run the benchmark CLI directly (without MCP):

```bash
total-recall eval benchmark
```

A PR that changes retrieval logic, scoring, or compaction thresholds must include a `--benchmark` run showing no regression against the `baseline` snapshot.

---

## PR Requirements

Before opening a pull request:

1. **Build clean** — `dotnet build src/TotalRecall.sln --configuration Release` exits 0 with **0 warnings** (CS1998 in test files OK only if pre-existing).
2. **Tests pass** — `dotnet test src/TotalRecall.sln --no-build` exits 0 with all tests green. Current baseline: 944 passing.
3. **AOT publish smoke test** (if you touched anything in the publish path) — `dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true` produces a binary that runs `status` against a scratch DB without errors.
4. **Benchmark does not regress** — run `/total-recall:commands eval --compare baseline` and include the output in your PR description if you changed retrieval, scoring, or compaction logic.
5. **New behavior is tested** — new importers, parsers, and content types all require corresponding test files in the matching `tests/TotalRecall.*.Tests/` project.
6. **Plugin manifest version sync** — if you're cutting a release, all four `version` fields must match (`package.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json`). This is a standing rule documented in `AGENTS.md`.
7. **No `Co-Authored-By: Claude ...` trailers** in commit messages. Project-wide rule.

If you're adding a new host tool importer, include the `Detect()` logic rationale in your PR description — false positives will silently corrupt imports for users who don't have the tool installed.
