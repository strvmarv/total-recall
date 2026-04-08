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
/// Imports memory + knowledge entries from a Claude Code installation.
/// Scans <c>{basePath}/projects/*/memory/*.md</c> (excluding the
/// <c>MEMORY.md</c> index file) for per-project memories and
/// <c>{basePath}/CLAUDE.md</c> for top-level knowledge. Mirrors
/// <c>src-ts/importers/claude-code.ts</c> bit-for-bit — including the
/// quirk that per-project <c>CLAUDE.md</c> files are COUNTED by
/// <see cref="Scan"/> but only the top-level <c>CLAUDE.md</c> is actually
/// imported by <see cref="ImportKnowledge"/>.
///
/// Frontmatter drives routing: <c>type=reference</c> → cold/knowledge,
/// everything else → warm/memory. A frontmatter <c>name</c> becomes the
/// entry's single tag.
/// </summary>
public sealed class ClaudeCodeImporter : IImporter
{
    private readonly ISqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly ImportLog _importLog;
    private readonly string _basePath;

    public string Name => "claude-code";

    public ClaudeCodeImporter(
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
        _basePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
    }

    public bool Detect() =>
        Directory.Exists(_basePath) &&
        Directory.Exists(Path.Combine(_basePath, "projects"));

    public ImporterScanResult Scan()
    {
        var memoryFiles = 0;
        var knowledgeFiles = 0;
        var sessionFiles = 0;

        var projectsDir = Path.Combine(_basePath, "projects");
        if (!Directory.Exists(projectsDir))
        {
            return new ImporterScanResult(0, 0, 0);
        }

        foreach (var projectDir in Directory.EnumerateDirectories(projectsDir))
        {
            var memoryDir = Path.Combine(projectDir, "memory");
            if (Directory.Exists(memoryDir))
            {
                foreach (var f in Directory.EnumerateFiles(memoryDir))
                {
                    if (Path.GetExtension(f) == ".md" &&
                        Path.GetFileName(f) != "MEMORY.md")
                    {
                        memoryFiles++;
                    }
                }
            }

            if (File.Exists(Path.Combine(projectDir, "CLAUDE.md")))
            {
                knowledgeFiles++;
            }

            foreach (var f in Directory.EnumerateFiles(projectDir))
            {
                if (Path.GetExtension(f) == ".jsonl")
                {
                    sessionFiles++;
                }
            }
        }

        return new ImporterScanResult(memoryFiles, knowledgeFiles, sessionFiles);
    }

    public ImportResult ImportMemories(string? project = null)
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        var projectsDir = Path.Combine(_basePath, "projects");
        if (!Directory.Exists(projectsDir))
        {
            return new ImportResult(imported, skipped, errors);
        }

        foreach (var projectDir in Directory.EnumerateDirectories(projectsDir))
        {
            var memoryDir = Path.Combine(projectDir, "memory");
            if (!Directory.Exists(memoryDir)) continue;

            foreach (var filePath in Directory.EnumerateFiles(memoryDir))
            {
                if (Path.GetExtension(filePath) != ".md") continue;
                if (Path.GetFileName(filePath) == "MEMORY.md") continue;

                try
                {
                    var raw = File.ReadAllText(filePath);
                    var hash = ImportLog.ContentHash(raw);

                    if (_importLog.IsAlreadyImported(hash))
                    {
                        skipped++;
                        continue;
                    }

                    var parsed = ImportUtils.ParseFrontmatter(raw);
                    var fm = parsed.Frontmatter;

                    var tier = Tier.Warm;
                    var type = ContentType.Memory;
                    if (fm?.Type == "reference")
                    {
                        tier = Tier.Cold;
                        type = ContentType.Knowledge;
                    }

                    IReadOnlyList<string> tags =
                        !string.IsNullOrEmpty(fm?.Name)
                            ? new[] { fm!.Name! }
                            : Array.Empty<string>();

                    var entryId = _store.Insert(tier, type, new InsertEntryOpts(
                        Content: parsed.Content,
                        Summary: fm?.Description,
                        Source: filePath,
                        SourceTool: SourceTool.ClaudeCode,
                        Project: project,
                        Tags: tags));

                    var embedding = _embedder.Embed(parsed.Content);
                    _vectorSearch.InsertEmbedding(tier, type, entryId, embedding);
                    _importLog.LogImport(
                        "claude-code", filePath, hash, entryId, tier, type);
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{filePath}: {ex.Message}");
                }
            }
        }

        return new ImportResult(imported, skipped, errors);
    }

    public ImportResult ImportKnowledge()
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        var claudeMdPath = Path.Combine(_basePath, "CLAUDE.md");
        if (!File.Exists(claudeMdPath))
        {
            return new ImportResult(imported, skipped, errors);
        }

        try
        {
            var raw = File.ReadAllText(claudeMdPath);
            var hash = ImportLog.ContentHash(raw);

            if (_importLog.IsAlreadyImported(hash))
            {
                skipped++;
                return new ImportResult(imported, skipped, errors);
            }

            var parsed = ImportUtils.ParseFrontmatter(raw);
            var tier = Tier.Warm;
            var type = ContentType.Knowledge;

            var entryId = _store.Insert(tier, type, new InsertEntryOpts(
                Content: parsed.Content,
                Source: claudeMdPath,
                SourceTool: SourceTool.ClaudeCode,
                Tags: new[] { "pinned" }));

            var embedding = _embedder.Embed(parsed.Content);
            _vectorSearch.InsertEmbedding(tier, type, entryId, embedding);
            _importLog.LogImport(
                "claude-code", claudeMdPath, hash, entryId, tier, type);
            imported++;
        }
        catch (Exception ex)
        {
            errors.Add($"{claudeMdPath}: {ex.Message}");
        }

        return new ImportResult(imported, skipped, errors);
    }
}
