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
    private readonly IStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly IImportLog _importLog;
    private readonly string _basePath;

    public string Name => "claude-code";

    public ClaudeCodeImporter(
        IStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        IImportLog importLog,
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

                // Pre-read frontmatter to decide tier/type routing. We
                // re-read inside ImportMarkdownFile; the TS reference does
                // the same — the file is tiny and dedupe happens on the
                // second read via the content hash.
                var tier = Tier.Warm;
                var type = ContentType.Memory;
                try
                {
                    var peek = File.ReadAllText(filePath);
                    var parsed = ImportUtils.ParseFrontmatter(peek);
                    if (parsed.Frontmatter?.Type == "reference")
                    {
                        tier = Tier.Cold;
                        type = ContentType.Knowledge;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{filePath}: {ex.Message}");
                    continue;
                }

                var outcome = ImportUtils.ImportMarkdownFile(
                    _store, _embedder, _vectorSearch, _importLog,
                    Name, SourceTool.ClaudeCode,
                    filePath, tier, type,
                    baseTags: Array.Empty<string>(),
                    prependFrontmatterName: true,
                    parseFrontmatter: true,
                    project: project);
                ImportUtils.Tally(outcome, ref imported, ref skipped, errors);
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

        var outcome = ImportUtils.ImportMarkdownFile(
            _store, _embedder, _vectorSearch, _importLog,
            Name, SourceTool.ClaudeCode,
            claudeMdPath, Tier.Warm, ContentType.Knowledge,
            baseTags: new[] { "pinned" },
            prependFrontmatterName: false,
            parseFrontmatter: true);
        ImportUtils.Tally(outcome, ref imported, ref skipped, errors);

        return new ImportResult(imported, skipped, errors);
    }
}
