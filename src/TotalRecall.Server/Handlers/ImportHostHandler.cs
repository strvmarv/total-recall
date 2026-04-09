// src/TotalRecall.Server/Handlers/ImportHostHandler.cs
//
// Plan 6 Task 6.0d — ports `total-recall import-host` to MCP. Iterates
// the injected set of IImporter implementations, runs detect/scan/import,
// and returns a per-source summary list. Mirrors the CLI --json shape
// from ImportHostCommand (but source-gen-serialized rather than
// hand-rolled so the Server stays AOT-clean through JsonContext).
//
// Args:
//   source  (string, optional) — restrict to a single importer by Name.
//   dry_run (bool,   optional, default false) — if true, run Detect+Scan
//           only, skip ImportMemories/ImportKnowledge.
//
// Production provider (in composition root / Host) wires the 7 real
// importers against SqliteStore, Embedder, VectorSearch, ImportLog. The
// test seam takes a pre-built IReadOnlyList<IImporter> so unit tests do
// not need to touch ONNX/Sqlite.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

/// <summary>Test seam for <see cref="ImportHostHandler"/>.</summary>
public delegate IReadOnlyList<IImporter> ImporterSetProvider();

public sealed class ImportHostHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "source":  {"type":"string","description":"Restrict to a single importer by name"},
            "dry_run": {"type":"boolean","description":"Detect+scan only; skip import"}
          }
        }
        """).RootElement.Clone();

    private readonly ImporterSetProvider _provider;

    public ImportHostHandler()
    {
        _provider = BuildProductionImporters;
    }

    /// <summary>Test/composition seam.</summary>
    public ImportHostHandler(ImporterSetProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "import_host";
    public string Description => "Detect and import memories/knowledge from installed host tools";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        string? source = null;
        bool dryRun = false;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            if (args.TryGetProperty("source", out var sEl) && sEl.ValueKind == JsonValueKind.String)
            {
                var s = sEl.GetString();
                if (!string.IsNullOrEmpty(s)) source = s;
            }
            if (args.TryGetProperty("dry_run", out var drEl))
            {
                if (drEl.ValueKind != JsonValueKind.True && drEl.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("dry_run must be a boolean");
                dryRun = drEl.GetBoolean();
            }
        }

        ct.ThrowIfCancellationRequested();

        var importers = _provider();
        var results = new List<ImportHostSourceDto>(importers.Count);

        foreach (var importer in importers)
        {
            if (source is not null && !string.Equals(importer.Name, source, StringComparison.Ordinal))
                continue;

            bool detected = importer.Detect();
            if (!detected)
            {
                results.Add(new ImportHostSourceDto(
                    Source: importer.Name,
                    Detected: false,
                    MemoriesImported: 0,
                    KnowledgeImported: 0,
                    Skipped: 0,
                    Errors: Array.Empty<string>()));
                continue;
            }

            _ = importer.Scan(); // for parity with CLI; result currently unused here
            if (dryRun)
            {
                results.Add(new ImportHostSourceDto(
                    Source: importer.Name,
                    Detected: true,
                    MemoriesImported: 0,
                    KnowledgeImported: 0,
                    Skipped: 0,
                    Errors: Array.Empty<string>()));
                continue;
            }

            var mem = importer.ImportMemories();
            var know = importer.ImportKnowledge();
            var errors = new List<string>(mem.Errors.Count + know.Errors.Count);
            errors.AddRange(mem.Errors);
            errors.AddRange(know.Errors);

            results.Add(new ImportHostSourceDto(
                Source: importer.Name,
                Detected: true,
                MemoriesImported: mem.Imported,
                KnowledgeImported: know.Imported,
                Skipped: mem.Skipped + know.Skipped,
                Errors: errors.ToArray()));
        }

        var dto = new ImportHostResultDto(
            Results: results.ToArray(),
            Count: results.Count);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.ImportHostResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static IReadOnlyList<IImporter> BuildProductionImporters()
    {
        var dbPath = ConfigLoader.GetDbPath();
        var conn = SqliteConnection.Open(dbPath);
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);
        var vec = new VectorSearch(conn);
        var embedder = EmbedderFactory.CreateProduction();
        var importLog = new ImportLog(conn);
        var index = new HierarchicalIndex(store, embedder, vec, conn);
        var validator = new IngestValidator(embedder, vec, conn);
        var fileIngester = new FileIngester(index, validator);

        return new List<IImporter>
        {
            new ClaudeCodeImporter(store, embedder, vec, importLog),
            new CopilotCliImporter(store, embedder, vec, importLog),
            new CursorImporter(store, embedder, vec, importLog),
            new ClineImporter(store, embedder, vec, importLog),
            new OpenCodeImporter(store, embedder, vec, importLog),
            new HermesImporter(store, embedder, vec, importLog),
            new ProjectDocsImporter(fileIngester, index, importLog),
        };
    }
}
