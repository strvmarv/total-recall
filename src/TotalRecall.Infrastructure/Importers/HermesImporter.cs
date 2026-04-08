using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Imports memory + knowledge content from a Hermes agent installation
/// (NousResearch/hermes-agent). Source layout (under <c>~/.hermes</c> by
/// default — override via <c>HERMES_HOME</c> env var or constructor arg):
///
///   memories/MEMORY.md              §-delimited warm/memory entries, tags=[hermes-memory]
///   memories/USER.md                §-delimited warm/memory entries, tags=[hermes-user, user-profile]
///   SOUL.md                         warm/knowledge, tags=[hermes-soul], frontmatter parsed
///   skills/&lt;name&gt;/SKILL.md          cold/knowledge, tags=[hermes-skill, &lt;name&gt;], frontmatter parsed
///   state.db                        counted as a session file but NOT imported
///   config.yaml                     detect-only
///
/// The §-delimiter in MEMORY.md / USER.md is a paragraph separator on its
/// own line: <c>\n§\n</c>. Each non-empty piece becomes an independent
/// warm/memory entry with summary = piece[0..200]. Mirrors
/// <c>src-ts/importers/hermes.ts</c> bit-for-bit.
/// </summary>
public sealed class HermesImporter : IImporter
{
    private static readonly Regex SectionDelimiter =
        new(@"\n§\n", RegexOptions.Compiled);

    private readonly ISqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly ImportLog _importLog;
    private readonly string _basePath;

    public string Name => "hermes";

    public HermesImporter(
        ISqliteStore store,
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

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _basePath = basePath
            ?? Environment.GetEnvironmentVariable("HERMES_HOME")
            ?? Path.Combine(home, ".hermes");
    }

    public bool Detect() =>
        Directory.Exists(_basePath) && (
            File.Exists(Path.Combine(_basePath, "state.db")) ||
            Directory.Exists(Path.Combine(_basePath, "memories")) ||
            File.Exists(Path.Combine(_basePath, "config.yaml")));

    public ImporterScanResult Scan()
    {
        var memoryFiles = 0;
        var knowledgeFiles = 0;
        var sessionFiles = 0;

        var memoriesDir = Path.Combine(_basePath, "memories");
        if (Directory.Exists(memoriesDir))
        {
            if (File.Exists(Path.Combine(memoriesDir, "MEMORY.md"))) memoryFiles++;
            if (File.Exists(Path.Combine(memoriesDir, "USER.md"))) memoryFiles++;
        }

        var skillsDir = Path.Combine(_basePath, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var sub in Directory.EnumerateDirectories(skillsDir))
            {
                if (File.Exists(Path.Combine(sub, "SKILL.md"))) knowledgeFiles++;
            }
        }

        if (File.Exists(Path.Combine(_basePath, "SOUL.md"))) knowledgeFiles++;

        if (File.Exists(Path.Combine(_basePath, "state.db"))) sessionFiles = 1;

        return new ImporterScanResult(memoryFiles, knowledgeFiles, sessionFiles);
    }

    public ImportResult ImportMemories(string? project = null)
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        ImportMemoryFile(
            Path.Combine(_basePath, "memories", "MEMORY.md"),
            new[] { "hermes-memory" },
            project,
            ref imported, ref skipped, errors);

        ImportMemoryFile(
            Path.Combine(_basePath, "memories", "USER.md"),
            new[] { "hermes-user", "user-profile" },
            project,
            ref imported, ref skipped, errors);

        return new ImportResult(imported, skipped, errors);
    }

    public ImportResult ImportKnowledge()
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        // 1. SOUL.md → warm/knowledge
        var soulPath = Path.Combine(_basePath, "SOUL.md");
        if (File.Exists(soulPath))
        {
            var outcome = ImportUtils.ImportMarkdownFile(
                _store, _embedder, _vectorSearch, _importLog,
                Name, SourceTool.Hermes,
                soulPath, Tier.Warm, ContentType.Knowledge,
                baseTags: new[] { "hermes-soul" },
                prependFrontmatterName: true,
                parseFrontmatter: true);
            ImportUtils.Tally(outcome, ref imported, ref skipped, errors);
        }

        // 2. skills/<name>/SKILL.md → cold/knowledge
        var skillsDir = Path.Combine(_basePath, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var sub in Directory.EnumerateDirectories(skillsDir))
            {
                var skillPath = Path.Combine(sub, "SKILL.md");
                if (!File.Exists(skillPath)) continue;

                var skillName = Path.GetFileName(sub);
                var outcome = ImportUtils.ImportMarkdownFile(
                    _store, _embedder, _vectorSearch, _importLog,
                    Name, SourceTool.Hermes,
                    skillPath, Tier.Cold, ContentType.Knowledge,
                    baseTags: new[] { "hermes-skill", skillName },
                    prependFrontmatterName: true,
                    parseFrontmatter: true);
                ImportUtils.Tally(outcome, ref imported, ref skipped, errors);
            }
        }

        return new ImportResult(imported, skipped, errors);
    }

    /// <summary>
    /// Read a Hermes memory file, split on the <c>§</c> delimiter, and
    /// import each non-empty piece as a warm/memory entry. Uses one
    /// outer try/catch matching the TS reference: if the file read fails,
    /// the whole file is recorded as a single error entry; per-piece
    /// embed/insert failures abort the remaining pieces for this file.
    /// </summary>
    private void ImportMemoryFile(
        string filePath,
        string[] tags,
        string? project,
        ref int imported,
        ref int skipped,
        List<string> errors)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var raw = File.ReadAllText(filePath);

            foreach (var rawPiece in SectionDelimiter.Split(raw))
            {
                var piece = rawPiece.Trim();
                if (piece.Length == 0) continue;

                var hash = ImportLog.ContentHash(piece);
                if (_importLog.IsAlreadyImported(hash))
                {
                    skipped++;
                    continue;
                }

                var summaryLen = Math.Min(200, piece.Length);
                var summary = piece.Substring(0, summaryLen);

                var entryId = _store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(
                    Content: piece,
                    Summary: summary,
                    Source: filePath,
                    SourceTool: SourceTool.Hermes,
                    Project: project,
                    Tags: tags));

                var embedding = _embedder.Embed(piece);
                _vectorSearch.InsertEmbedding(Tier.Warm, ContentType.Memory, entryId, embedding);
                _importLog.LogImport(
                    Name, filePath, hash, entryId, Tier.Warm, ContentType.Memory);
                imported++;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{filePath}: {ex.Message}");
        }
    }
}
