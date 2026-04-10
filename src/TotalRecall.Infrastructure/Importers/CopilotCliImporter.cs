using System;
using System.Collections.Generic;
using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Imports GitHub Copilot CLI session plans into the cold knowledge tier.
/// Copilot CLI has no per-project "memories" concept — only session-state
/// plans. <see cref="ImportMemories"/> is therefore a no-op.
///
/// Source layout (under <c>~/.copilot/</c> by default):
///   session-state/
///     {sessionId}/
///       plan.md         — imported as cold/knowledge
///       *.jsonl         — counted as session files, NOT imported
///
/// Unlike <see cref="ClaudeCodeImporter"/>, no frontmatter parsing is
/// performed: <c>plan.md</c> content is inserted verbatim with no tags.
/// Mirrors <c>src-ts/importers/copilot-cli.ts</c> bit-for-bit.
/// </summary>
public sealed class CopilotCliImporter : IImporter
{
    private readonly IStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly ImportLog _importLog;
    private readonly string _basePath;

    public string Name => "copilot-cli";

    public CopilotCliImporter(
        IStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        ImportLog importLog,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(importLog);
        _store = store;
        _embedder = embedder;
        _vectorSearch = vectorSearch;
        _importLog = importLog;
        _basePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");
    }

    public bool Detect() =>
        Directory.Exists(_basePath) &&
        Directory.Exists(Path.Combine(_basePath, "session-state"));

    public ImporterScanResult Scan()
    {
        var knowledgeFiles = 0;
        var sessionFiles = 0;

        var sessionStateDir = Path.Combine(_basePath, "session-state");
        if (!Directory.Exists(sessionStateDir))
        {
            return new ImporterScanResult(0, 0, 0);
        }

        foreach (var sessionDir in Directory.EnumerateDirectories(sessionStateDir))
        {
            if (File.Exists(Path.Combine(sessionDir, "plan.md")))
            {
                knowledgeFiles++;
            }

            foreach (var f in Directory.EnumerateFiles(sessionDir))
            {
                if (Path.GetExtension(f) == ".jsonl")
                {
                    sessionFiles++;
                }
            }
        }

        return new ImporterScanResult(0, knowledgeFiles, sessionFiles);
    }

    /// <summary>Copilot CLI has no per-project memories. Returns empty.</summary>
    public ImportResult ImportMemories(string? project = null) => ImportResult.Empty;

    public ImportResult ImportKnowledge()
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        var sessionStateDir = Path.Combine(_basePath, "session-state");
        if (!Directory.Exists(sessionStateDir))
        {
            return new ImportResult(imported, skipped, errors);
        }

        foreach (var sessionDir in Directory.EnumerateDirectories(sessionStateDir))
        {
            var planPath = Path.Combine(sessionDir, "plan.md");
            if (!File.Exists(planPath)) continue;

            var outcome = ImportUtils.ImportMarkdownFile(
                _store, _embedder, _vectorSearch, _importLog,
                Name, SourceTool.CopilotCli,
                planPath, Tier.Cold, ContentType.Knowledge,
                baseTags: Array.Empty<string>(),
                prependFrontmatterName: false,
                parseFrontmatter: false);
            ImportUtils.Tally(outcome, ref imported, ref skipped, errors);
        }

        return new ImportResult(imported, skipped, errors);
    }
}
