using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Imports Cursor knowledge content. Cursor has no structured memory
/// system; <see cref="ImportMemories"/> is a no-op.
///
/// Source layout:
///   {configPath}/User/globalStorage/state.vscdb
///     ItemTable rows. The 'aicontext.personalContext' value is imported
///     verbatim as warm/knowledge with tags=[global-rules]; the row's
///     <c>source</c> column is the .vscdb path itself.
///   {configPath}/User/workspaceStorage/{ws}/workspace.json
///     JSON with optional <c>folder</c> and <c>workspace</c> file:// URLs
///     pointing at project worktrees. For each unique resolved project path:
///       {projectPath}/.cursorrules                  (cold/knowledge, tags=[cursorrules, legacy])
///       {projectPath}/.cursor/rules/*.mdc           (cold/knowledge, tags=[cursor-rule], frontmatter parsed)
///
/// Mirrors <c>src-ts/importers/cursor.ts</c> bit-for-bit. The state.vscdb
/// is opened with a separate <see cref="SqliteConnection"/> in read-only
/// mode (NOT through <see cref="Storage.SqliteConnection.Open"/>, which
/// loads sqlite-vec — that extension is unnecessary here).
/// </summary>
public sealed class CursorImporter : IImporter
{
    private readonly ISqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly ImportLog _importLog;
    private readonly string _configPath;
    private readonly string _extensionPath;

    public string Name => "cursor";

    public CursorImporter(
        ISqliteStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        ImportLog importLog,
        string? configPath = null,
        string? extensionPath = null)
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
        _configPath = configPath ?? Path.Combine(home, ".config", "Cursor");
        _extensionPath = extensionPath ?? Path.Combine(home, ".cursor");
    }

    public bool Detect() =>
        Directory.Exists(_configPath) || Directory.Exists(_extensionPath);

    public ImporterScanResult Scan()
    {
        var knowledgeFiles = 0;

        var globalDb = Path.Combine(_configPath, "User", "globalStorage", "state.vscdb");
        if (File.Exists(globalDb)) knowledgeFiles++;

        var workspaceDir = Path.Combine(_configPath, "User", "workspaceStorage");
        if (Directory.Exists(workspaceDir))
        {
            foreach (var sub in Directory.EnumerateDirectories(workspaceDir))
            {
                var wsJson = Path.Combine(sub, "workspace.json");
                if (!File.Exists(wsJson)) continue;

                string? projectPath;
                try
                {
                    projectPath = ParseProjectPath(wsJson);
                }
                catch
                {
                    continue;
                }
                if (projectPath is null) continue;

                if (File.Exists(Path.Combine(projectPath, ".cursorrules")))
                    knowledgeFiles++;

                var rulesDir = Path.Combine(projectPath, ".cursor", "rules");
                if (Directory.Exists(rulesDir))
                {
                    foreach (var f in Directory.EnumerateFiles(rulesDir))
                    {
                        if (Path.GetExtension(f) == ".mdc") knowledgeFiles++;
                    }
                }
            }
        }

        return new ImporterScanResult(0, knowledgeFiles, 0);
    }

    /// <summary>Cursor has no structured memory system. Returns empty.</summary>
    public ImportResult ImportMemories(string? project = null) => ImportResult.Empty;

    public ImportResult ImportKnowledge()
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        ImportGlobalRules(ref imported, ref skipped, errors);
        ImportProjectRules(ref imported, ref skipped, errors);

        return new ImportResult(imported, skipped, errors);
    }

    private void ImportGlobalRules(ref int imported, ref int skipped, List<string> errors)
    {
        var dbPath = Path.Combine(_configPath, "User", "globalStorage", "state.vscdb");
        if (!File.Exists(dbPath)) return;

        try
        {
            using var conn = new MsSqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'aicontext.personalContext'";
            string? content;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return;
                if (reader.IsDBNull(0)) return;
                content = reader.GetString(0);
            }
            if (string.IsNullOrEmpty(content)) return;

            var hash = ImportLog.ContentHash(content);
            if (_importLog.IsAlreadyImported(hash))
            {
                skipped++;
                return;
            }

            var entryId = _store.Insert(Tier.Warm, ContentType.Knowledge, new InsertEntryOpts(
                Content: content,
                Source: dbPath,
                SourceTool: SourceTool.Cursor,
                Tags: new[] { "global-rules" }));

            var embedding = _embedder.Embed(content);
            _vectorSearch.InsertEmbedding(Tier.Warm, ContentType.Knowledge, entryId, embedding);
            _importLog.LogImport(
                Name, dbPath, hash, entryId, Tier.Warm, ContentType.Knowledge);
            imported++;
        }
        catch (Exception ex)
        {
            errors.Add($"cursor global rules: {ex.Message}");
        }
    }

    private void ImportProjectRules(ref int imported, ref int skipped, List<string> errors)
    {
        var workspaceDir = Path.Combine(_configPath, "User", "workspaceStorage");
        if (!Directory.Exists(workspaceDir)) return;

        var projectPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in Directory.EnumerateDirectories(workspaceDir))
        {
            var wsJson = Path.Combine(sub, "workspace.json");
            if (!File.Exists(wsJson)) continue;
            try
            {
                var p = ParseProjectPath(wsJson);
                if (p is not null) projectPaths.Add(p);
            }
            catch
            {
                // skip unreadable workspace entries
            }
        }

        foreach (var projectPath in projectPaths)
        {
            var legacyPath = Path.Combine(projectPath, ".cursorrules");
            if (File.Exists(legacyPath))
            {
                ImportRuleFile(legacyPath, new[] { "cursorrules", "legacy" }, ref imported, ref skipped, errors);
            }

            var rulesDir = Path.Combine(projectPath, ".cursor", "rules");
            if (Directory.Exists(rulesDir))
            {
                foreach (var f in Directory.EnumerateFiles(rulesDir))
                {
                    if (Path.GetExtension(f) != ".mdc") continue;
                    ImportRuleFile(f, new[] { "cursor-rule" }, ref imported, ref skipped, errors);
                }
            }
        }
    }

    private void ImportRuleFile(
        string filePath,
        string[] baseTags,
        ref int imported,
        ref int skipped,
        List<string> errors)
    {
        try
        {
            var raw = File.ReadAllText(filePath);
            var hash = ImportLog.ContentHash(raw);

            if (_importLog.IsAlreadyImported(hash))
            {
                skipped++;
                return;
            }

            var parsed = ImportUtils.ParseFrontmatter(raw);
            var fm = parsed.Frontmatter;

            string[] tags = !string.IsNullOrEmpty(fm?.Name)
                ? new[] { fm!.Name! }.Concat(baseTags).ToArray()
                : baseTags;

            var entryId = _store.Insert(Tier.Cold, ContentType.Knowledge, new InsertEntryOpts(
                Content: parsed.Content,
                Summary: fm?.Description,
                Source: filePath,
                SourceTool: SourceTool.Cursor,
                Tags: tags));

            var embedding = _embedder.Embed(parsed.Content);
            _vectorSearch.InsertEmbedding(Tier.Cold, ContentType.Knowledge, entryId, embedding);
            _importLog.LogImport(
                Name, filePath, hash, entryId, Tier.Cold, ContentType.Knowledge);
            imported++;
        }
        catch (Exception ex)
        {
            errors.Add($"{filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Read a workspace.json file and resolve its <c>folder</c> (preferred)
    /// or <c>workspace</c> field from a file:// URL to a filesystem path.
    /// Returns null if neither field is present or both fail to parse.
    /// </summary>
    private static string? ParseProjectPath(string wsJsonPath)
    {
        var json = File.ReadAllText(wsJsonPath);
        var dto = JsonSerializer.Deserialize(json, CursorJsonContext.Default.CursorWorkspaceDto);
        if (dto is null) return null;
        if (!string.IsNullOrEmpty(dto.Folder))
        {
            var p = SafeFileUrlToPath(dto.Folder!);
            if (p is not null) return p;
        }
        if (!string.IsNullOrEmpty(dto.Workspace))
        {
            return SafeFileUrlToPath(dto.Workspace!);
        }
        return null;
    }

    private static string? SafeFileUrlToPath(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.IsFile ? uri.LocalPath : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// AOT-safe shape for Cursor's workspace.json files. Only the two URL
/// fields are used; everything else is ignored.
/// </summary>
internal sealed class CursorWorkspaceDto
{
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(CursorWorkspaceDto))]
internal sealed partial class CursorJsonContext : JsonSerializerContext
{
}
