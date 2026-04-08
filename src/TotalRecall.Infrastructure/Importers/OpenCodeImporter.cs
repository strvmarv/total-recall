using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Imports OpenCode knowledge content. OpenCode has no per-project memory
/// store — only configuration / agent / command markdown files —
/// so <see cref="ImportMemories"/> is a no-op.
///
/// Source layout:
///   {configPath}/AGENTS.md            (global, warm/knowledge, tags=[agents-md, global])
///   {dataPath}/opencode.db            (sqlite — read-only — used to discover project worktrees)
///   {projectPath}/.opencode/agent/*.md       (cold/knowledge, tags=[opencode-agent])
///   {projectPath}/.opencode/command/*.md     (cold/knowledge, tags=[opencode-command])
///   {projectPath}/AGENTS.md           (cold/knowledge, tags=[agents-md, project])
///
/// The opencode.db is opened with a separate <see cref="SqliteConnection"/>
/// in read-only mode (NOT through <see cref="Storage.SqliteConnection.Open"/>,
/// which loads sqlite-vec — that extension is unnecessary here). Failures to
/// open or query the DB are swallowed and yield an empty project list.
/// Mirrors <c>src-ts/importers/opencode.ts</c> bit-for-bit.
/// </summary>
public sealed class OpenCodeImporter : IImporter
{
    private readonly ISqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly ImportLog _importLog;
    private readonly string _dataPath;
    private readonly string _configPath;

    public string Name => "opencode";

    public OpenCodeImporter(
        ISqliteStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        ImportLog importLog,
        string? dataPath = null,
        string? configPath = null)
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
        _dataPath = dataPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"),
            "opencode");
        _configPath = configPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"),
            "opencode");
    }

    public bool Detect() =>
        Directory.Exists(_dataPath) || Directory.Exists(_configPath);

    public ImporterScanResult Scan()
    {
        var knowledgeFiles = 0;
        var sessionFiles = 0;

        if (File.Exists(Path.Combine(_configPath, "AGENTS.md")))
            knowledgeFiles++;

        var dbPath = Path.Combine(_dataPath, "opencode.db");
        if (File.Exists(dbPath))
            sessionFiles = 1;

        return new ImporterScanResult(0, knowledgeFiles, sessionFiles);
    }

    /// <summary>OpenCode has no per-project memories. Returns empty.</summary>
    public ImportResult ImportMemories(string? project = null) => ImportResult.Empty;

    public ImportResult ImportKnowledge()
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        ImportAgentsMd(ref imported, ref skipped, errors);
        ImportProjectContent(ref imported, ref skipped, errors);

        return new ImportResult(imported, skipped, errors);
    }

    private void ImportAgentsMd(ref int imported, ref int skipped, List<string> errors)
    {
        var agentsMdPath = Path.Combine(_configPath, "AGENTS.md");
        if (!File.Exists(agentsMdPath)) return;

        try
        {
            var raw = File.ReadAllText(agentsMdPath);
            var hash = ImportLog.ContentHash(raw);

            if (_importLog.IsAlreadyImported(hash))
            {
                skipped++;
                return;
            }

            var parsed = ImportUtils.ParseFrontmatter(raw);

            var entryId = _store.Insert(Tier.Warm, ContentType.Knowledge, new InsertEntryOpts(
                Content: parsed.Content,
                Source: agentsMdPath,
                SourceTool: SourceTool.Opencode,
                Tags: new[] { "agents-md", "global" }));

            var embedding = _embedder.Embed(parsed.Content);
            _vectorSearch.InsertEmbedding(Tier.Warm, ContentType.Knowledge, entryId, embedding);
            _importLog.LogImport(
                Name, agentsMdPath, hash, entryId, Tier.Warm, ContentType.Knowledge);
            imported++;
        }
        catch (Exception ex)
        {
            errors.Add($"{agentsMdPath}: {ex.Message}");
        }
    }

    private void ImportProjectContent(ref int imported, ref int skipped, List<string> errors)
    {
        var projectPaths = DiscoverProjects();

        foreach (var projectPath in projectPaths)
        {
            var openCodeDir = Path.Combine(projectPath, ".opencode");
            if (!Directory.Exists(openCodeDir)) continue;

            var agentDir = Path.Combine(openCodeDir, "agent");
            if (Directory.Exists(agentDir))
            {
                ImportMdDir(agentDir, new[] { "opencode-agent" }, ref imported, ref skipped, errors);
            }

            var commandDir = Path.Combine(openCodeDir, "command");
            if (Directory.Exists(commandDir))
            {
                ImportMdDir(commandDir, new[] { "opencode-command" }, ref imported, ref skipped, errors);
            }

            var projectAgentsMd = Path.Combine(projectPath, "AGENTS.md");
            if (File.Exists(projectAgentsMd))
            {
                ImportSingleFile(projectAgentsMd, new[] { "agents-md", "project" }, ref imported, ref skipped, errors);
            }
        }
    }

    private List<string> DiscoverProjects()
    {
        var dbPath = Path.Combine(_dataPath, "opencode.db");
        if (!File.Exists(dbPath)) return new List<string>();

        var paths = new List<string>();
        try
        {
            using var conn = new MsSqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT worktree FROM project";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var p = reader.GetString(0);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                    paths.Add(p);
            }
        }
        catch (SqliteException)
        {
            // Missing/corrupt/locked DB → empty list, mirroring TS behaviour.
            return new List<string>();
        }
        catch (InvalidOperationException)
        {
            return new List<string>();
        }
        return paths;
    }

    private void ImportMdDir(
        string dir,
        string[] tags,
        ref int imported,
        ref int skipped,
        List<string> errors)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (Path.GetExtension(f) != ".md") continue;
            ImportSingleFile(f, tags, ref imported, ref skipped, errors);
        }
    }

    private void ImportSingleFile(
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
                SourceTool: SourceTool.Opencode,
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
}
