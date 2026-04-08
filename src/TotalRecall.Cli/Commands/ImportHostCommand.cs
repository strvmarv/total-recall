// src/TotalRecall.Cli/Commands/ImportHostCommand.cs
//
// Plan 5 Task 5.9 — `total-recall import-host [--source N] [--json]`.
// Ports src-ts/tools/import-tools.ts to the .NET CLI. Iterates all 7
// Infrastructure IImporter implementations, detects + scans + imports
// memories/knowledge from each.
//
// Default output: Spectre table summarizing per-tool detection, scan
// counts, and import/skip/error tallies. --json emits a structured
// response shape matching the TS import_host tool.
//
// Test seam: public ctor takes IEnumerable<IImporter> + TextWriter, so
// tests can inject fake importers without instantiating real
// infrastructure (ONNX model, Sqlite, etc.).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Cli.Internal;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands;

public sealed class ImportHostCommand : ICliCommand
{
    // Canonical list of importer names, in display order. Kept in sync
    // with the 7 concrete IImporter implementations under
    // src/TotalRecall.Infrastructure/Importers/.
    private static readonly string[] ValidSources =
    {
        "claude-code",
        "copilot-cli",
        "cursor",
        "cline",
        "opencode",
        "hermes",
        "project-docs",
    };

    private readonly IReadOnlyList<IImporter>? _importers;
    private readonly TextWriter? _out;

    public ImportHostCommand() { }

    // Test/composition seam.
    public ImportHostCommand(IEnumerable<IImporter> importers, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(importers);
        _importers = new List<IImporter>(importers);
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "import-host";
    public string? Group => null;
    public string Description => "Detect and import memories/knowledge from installed host tools";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        string? source = null;
        bool emitJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--source":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("import-host: --source requires a value");
                        return 2;
                    }
                    source = args[++i];
                    break;
                case "--json":
                    emitJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"import-host: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        if (source is not null && Array.IndexOf(ValidSources, source) < 0)
        {
            Console.Error.WriteLine($"import-host: unknown --source '{source}'");
            Console.Error.WriteLine("  valid sources: " + string.Join(", ", ValidSources));
            return 2;
        }

        IReadOnlyList<IImporter> importers;
        MsSqliteConnection? owned = null;
        try
        {
            if (_importers is not null)
            {
                importers = _importers;
            }
            else
            {
                var dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                var store = new SqliteStore(owned);
                var vec = new VectorSearch(owned);
                var embedder = EmbedderFactory.CreateProduction();
                var importLog = new ImportLog(owned);
                var index = new HierarchicalIndex(store, embedder, vec, owned);
                var validator = new IngestValidator(embedder, vec, owned);
                var fileIngester = new FileIngester(index, validator);

                importers = new List<IImporter>
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"import-host: failed to initialize: {ex.Message}");
            owned?.Dispose();
            return 1;
        }

        try
        {
            var results = new List<PerToolResult>();
            foreach (var importer in importers)
            {
                if (source is not null &&
                    !string.Equals(importer.Name, source, StringComparison.Ordinal))
                {
                    continue;
                }

                bool detected;
                try
                {
                    detected = importer.Detect();
                }
                catch (Exception ex)
                {
                    results.Add(new PerToolResult(
                        Tool: importer.Name,
                        Detected: false,
                        Scan: null,
                        Memories: null,
                        Knowledge: null,
                        ToolError: ex.Message));
                    continue;
                }

                if (!detected)
                {
                    results.Add(new PerToolResult(
                        Tool: importer.Name,
                        Detected: false,
                        Scan: null,
                        Memories: null,
                        Knowledge: null,
                        ToolError: null));
                    continue;
                }

                ImporterScanResult? scan = null;
                ImportResult? mem = null;
                ImportResult? know = null;
                string? toolErr = null;
                try
                {
                    scan = importer.Scan();
                    mem = importer.ImportMemories();
                    know = importer.ImportKnowledge();
                }
                catch (Exception ex)
                {
                    toolErr = ex.Message;
                }

                results.Add(new PerToolResult(
                    Tool: importer.Name,
                    Detected: true,
                    Scan: scan,
                    Memories: mem,
                    Knowledge: know,
                    ToolError: toolErr));
            }

            if (emitJson)
            {
                var json = SerializeJson(results);
                if (_out is not null) _out.WriteLine(json);
                else Console.Out.WriteLine(json);
                return 0;
            }

            Render(results);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"import-host: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    internal sealed record PerToolResult(
        string Tool,
        bool Detected,
        ImporterScanResult? Scan,
        ImportResult? Memories,
        ImportResult? Knowledge,
        string? ToolError);

    private void Render(IReadOnlyList<PerToolResult> results)
    {
        var table = new Table().Title("[bold]host importers[/]");
        table.AddColumn("Tool");
        table.AddColumn("Detected");
        table.AddColumn(new TableColumn("Mem Files").RightAligned());
        table.AddColumn(new TableColumn("KB Files").RightAligned());
        table.AddColumn("Imported (M/K)");
        table.AddColumn("Skipped (M/K)");
        table.AddColumn(new TableColumn("Errors").RightAligned());

        foreach (var r in results)
        {
            var detected = r.Detected ? "yes" : "no";
            var memFiles = r.Scan?.MemoryFiles.ToString(CultureInfo.InvariantCulture) ?? "-";
            var kbFiles = r.Scan?.KnowledgeFiles.ToString(CultureInfo.InvariantCulture) ?? "-";
            var importedPair = r.Detected
                ? $"{(r.Memories?.Imported ?? 0)}/{(r.Knowledge?.Imported ?? 0)}"
                : "-";
            var skippedPair = r.Detected
                ? $"{(r.Memories?.Skipped ?? 0)}/{(r.Knowledge?.Skipped ?? 0)}"
                : "-";
            var errCount = ((r.Memories?.Errors.Count ?? 0) + (r.Knowledge?.Errors.Count ?? 0))
                .ToString(CultureInfo.InvariantCulture);

            table.AddRow(
                Markup.Escape(r.Tool),
                detected,
                memFiles,
                kbFiles,
                importedPair,
                skippedPair,
                errCount);
        }

        AnsiConsole.Write(table);
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(IReadOnlyList<PerToolResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("{\"results\":[");
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = results[i];
            sb.Append('{');
            AppendString(sb, "tool"); sb.Append(':'); AppendString(sb, r.Tool); sb.Append(',');
            AppendString(sb, "detected"); sb.Append(':'); sb.Append(r.Detected ? "true" : "false");

            sb.Append(',');
            AppendString(sb, "scan"); sb.Append(':');
            if (r.Scan is null) sb.Append("null");
            else
            {
                sb.Append('{');
                AppendIntField(sb, "memory_files", r.Scan.MemoryFiles); sb.Append(',');
                AppendIntField(sb, "knowledge_files", r.Scan.KnowledgeFiles); sb.Append(',');
                AppendIntField(sb, "session_files", r.Scan.SessionFiles);
                sb.Append('}');
            }

            sb.Append(',');
            AppendString(sb, "memories_result"); sb.Append(':');
            AppendImportResult(sb, r.Memories);

            sb.Append(',');
            AppendString(sb, "knowledge_result"); sb.Append(':');
            AppendImportResult(sb, r.Knowledge);

            if (r.ToolError is not null)
            {
                sb.Append(',');
                AppendString(sb, "error"); sb.Append(':'); AppendString(sb, r.ToolError);
            }

            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void AppendImportResult(StringBuilder sb, ImportResult? r)
    {
        if (r is null) { sb.Append("null"); return; }
        sb.Append('{');
        AppendIntField(sb, "imported", r.Imported); sb.Append(',');
        AppendIntField(sb, "skipped", r.Skipped); sb.Append(',');
        AppendString(sb, "errors"); sb.Append(":[");
        for (int j = 0; j < r.Errors.Count; j++)
        {
            if (j > 0) sb.Append(',');
            AppendString(sb, r.Errors[j]);
        }
        sb.Append(']');
        sb.Append('}');
    }

    private static void AppendIntField(StringBuilder sb, string name, int value)
    {
        AppendString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendString(StringBuilder sb, string s) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall import-host [--source <name>] [--json]");
        w.WriteLine("  valid sources: " + string.Join(", ", ValidSources));
    }
}
