using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Imports Cline knowledge content. Cline has no structured memory system;
/// <see cref="ImportMemories"/> is a no-op.
///
/// Source layout:
///   {dataPath}/                       new (v3+) shared storage root
///   {legacyPath}/                     legacy VS Code globalStorage root
///     state/taskHistory.json          task history index (cold/knowledge per item)
///     settings/cline_mcp_settings.json (counted as knowledge in Scan)
///   {globalRulesPath}/*.md|*.txt      ~/Documents/Cline/Rules (warm/knowledge, raw text)
///   {globalRulesFallback}/*.md|*.txt  ~/Cline/Rules            (warm/knowledge, raw text)
///
/// Mirrors <c>src-ts/importers/cline.ts</c> bit-for-bit, with one
/// deviation: <c>globalRulesPath</c> and <c>globalRulesFallback</c> are
/// constructor parameters (the TS hardcodes them) so tests can stay
/// hermetic without touching the real <c>~/Documents</c> directory.
/// </summary>
public sealed class ClineImporter : IImporter
{
    private readonly IStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly ImportLog _importLog;
    private readonly string _dataPath;
    private readonly string _legacyPath;
    private readonly string _globalRulesPath;
    private readonly string _globalRulesFallback;

    public string Name => "cline";

    public ClineImporter(
        IStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        ImportLog importLog,
        string? dataPath = null,
        string? legacyPath = null,
        string? globalRulesPath = null,
        string? globalRulesFallback = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(importLog);
        _store = store;
        _embedder = embedder;
        _vectorSearch = vectorSearch;
        _importLog = importLog;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _dataPath = dataPath ?? Path.Combine(home, ".cline", "data");
        _legacyPath = legacyPath ?? Path.Combine(
            home, ".config", "Code", "User", "globalStorage", "saoudrizwan.claude-dev");
        _globalRulesPath = globalRulesPath ?? Path.Combine(home, "Documents", "Cline", "Rules");
        _globalRulesFallback = globalRulesFallback ?? Path.Combine(home, "Cline", "Rules");
    }

    public bool Detect() =>
        Directory.Exists(_dataPath) || Directory.Exists(_legacyPath);

    public ImporterScanResult Scan()
    {
        var knowledgeFiles = 0;
        var sessionFiles = 0;

        foreach (var dir in new[] { _globalRulesPath, _globalRulesFallback })
        {
            if (Directory.Exists(dir))
                knowledgeFiles += CountFiles(dir, new[] { ".md", ".txt" });
        }

        var stateDir = ResolveStateDir();
        if (stateDir is not null)
        {
            var historyPath = Path.Combine(stateDir, "taskHistory.json");
            if (File.Exists(historyPath))
            {
                try
                {
                    var json = File.ReadAllText(historyPath);
                    var items = JsonSerializer.Deserialize(
                        json, ClineJsonContext.Default.ClineTaskHistoryItemArray);
                    if (items is not null) sessionFiles = items.Length;
                }
                catch
                {
                    // ignore unreadable history
                }
            }
        }

        var dataDir = ResolveDataDir();
        if (dataDir is not null)
        {
            var mcpSettings = Path.Combine(dataDir, "settings", "cline_mcp_settings.json");
            if (File.Exists(mcpSettings)) knowledgeFiles++;
        }

        return new ImporterScanResult(0, knowledgeFiles, sessionFiles);
    }

    /// <summary>Cline has no structured memory system. Returns empty.</summary>
    public ImportResult ImportMemories(string? project = null) => ImportResult.Empty;

    public ImportResult ImportKnowledge()
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        ImportGlobalRules(ref imported, ref skipped, errors);
        ImportTaskSummaries(ref imported, ref skipped, errors);

        return new ImportResult(imported, skipped, errors);
    }

    private string? ResolveDataDir()
    {
        if (Directory.Exists(_dataPath)) return _dataPath;
        if (Directory.Exists(_legacyPath)) return _legacyPath;
        return null;
    }

    private string? ResolveStateDir()
    {
        var dataDir = ResolveDataDir();
        if (dataDir is null) return null;
        var newState = Path.Combine(dataDir, "state");
        if (Directory.Exists(newState)) return newState;
        return dataDir;
    }

    private void ImportGlobalRules(ref int imported, ref int skipped, List<string> errors)
    {
        foreach (var dir in new[] { _globalRulesPath, _globalRulesFallback })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var filePath in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(filePath);
                if (ext != ".md" && ext != ".txt") continue;

                // Cline rules are imported VERBATIM — no frontmatter parsing.
                var outcome = ImportUtils.ImportMarkdownFile(
                    _store, _embedder, _vectorSearch, _importLog,
                    Name, SourceTool.Cline,
                    filePath, Tier.Warm, ContentType.Knowledge,
                    baseTags: new[] { "cline-rule", "global" },
                    prependFrontmatterName: false,
                    parseFrontmatter: false);
                ImportUtils.Tally(outcome, ref imported, ref skipped, errors);
            }
        }
    }

    private void ImportTaskSummaries(ref int imported, ref int skipped, List<string> errors)
    {
        var stateDir = ResolveStateDir();
        if (stateDir is null) return;

        var historyPath = Path.Combine(stateDir, "taskHistory.json");
        if (!File.Exists(historyPath)) return;

        ClineTaskHistoryItem[]? items;
        try
        {
            var json = File.ReadAllText(historyPath);
            items = JsonSerializer.Deserialize(
                json, ClineJsonContext.Default.ClineTaskHistoryItemArray);
        }
        catch
        {
            return;
        }
        if (items is null) return;

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Task) || string.IsNullOrEmpty(item.Id)) continue;

            var parts = new List<string> { $"Task: {item.Task}" };
            if (!string.IsNullOrEmpty(item.ModelId))
                parts.Add($"Model: {item.ModelId}");
            if (item.TotalCost.HasValue && item.TotalCost.Value != 0.0)
                parts.Add($"Cost: ${item.TotalCost.Value.ToString("F4", CultureInfo.InvariantCulture)}");
            if (item.Ts.HasValue && item.Ts.Value != 0)
            {
                var date = DateTimeOffset.FromUnixTimeMilliseconds(item.Ts.Value)
                    .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                parts.Add($"Date: {date}");
            }

            var content = string.Join("\n", parts);
            var hash = ImportLog.ContentHash(content);

            if (_importLog.IsAlreadyImported(hash))
            {
                skipped++;
                continue;
            }

            try
            {
                var summaryLen = Math.Min(200, item.Task!.Length);
                var summary = item.Task.Substring(0, summaryLen);

                var entryId = _store.Insert(Tier.Cold, ContentType.Knowledge, new InsertEntryOpts(
                    Content: content,
                    Summary: summary,
                    Source: $"cline:task:{item.Id}",
                    SourceTool: SourceTool.Cline,
                    Tags: new[] { "cline-task" }));

                var embedding = _embedder.Embed(content);
                _vectorSearch.InsertEmbedding(Tier.Cold, ContentType.Knowledge, entryId, embedding);
                _importLog.LogImport(
                    Name, $"task:{item.Id}", hash, entryId, Tier.Cold, ContentType.Knowledge);
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"cline task {item.Id}: {ex.Message}");
            }
        }
    }

    private static int CountFiles(string dir, string[] extensions)
    {
        var count = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(f);
            for (var i = 0; i < extensions.Length; i++)
            {
                if (ext == extensions[i]) { count++; break; }
            }
        }
        return count;
    }
}

/// <summary>
/// Shape of a single entry in Cline's <c>taskHistory.json</c>. Tokens
/// in/out are present in the source but unused.
/// </summary>
internal sealed class ClineTaskHistoryItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("ts")] public long? Ts { get; set; }
    [JsonPropertyName("totalCost")] public double? TotalCost { get; set; }
    [JsonPropertyName("modelId")] public string? ModelId { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(ClineTaskHistoryItem[]))]
internal sealed partial class ClineJsonContext : JsonSerializerContext
{
}
