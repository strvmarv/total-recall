using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Wraps <see cref="FileIngester"/> as an <see cref="IImporter"/> for
/// project documentation. Collects the top-level README/CONTRIBUTING/CLAUDE/
/// AGENTS markdown files plus every <c>.md</c> under <c>docs/</c> or
/// <c>doc/</c> (recursive) and funnels them through the
/// chunker + HierarchicalIndex pipeline.
///
/// Differs from the other six importers: they write individual entries via
/// <c>ISqliteStore.Insert</c>, whereas this one produces a collection ->
/// document -> chunk tree in cold/knowledge. Mirrors
/// <c>src-ts/importers/project-docs.ts</c>.
///
/// TS-parity quirk: the <c>import_log.target_entry_id</c> for each file is
/// the collection id (not the document id), matching the TS reference's
/// <c>logIngest(db, filePath, hash, collectionId)</c> call.
/// </summary>
public sealed class ProjectDocsImporter : IImporter
{
    private static readonly string[] DocFiles =
        { "README.md", "CONTRIBUTING.md", "CLAUDE.md", "AGENTS.md" };

    private static readonly string[] DocDirs = { "docs", "doc" };

    private readonly FileIngester _ingester;
    private readonly HierarchicalIndex _index;
    private readonly ImportLog _importLog;
    private readonly string _cwd;

    public string Name => "project-docs";

    public ProjectDocsImporter(
        FileIngester ingester,
        HierarchicalIndex index,
        ImportLog importLog,
        string? cwd = null)
    {
        ArgumentNullException.ThrowIfNull(ingester);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(importLog);
        _ingester = ingester;
        _index = index;
        _importLog = importLog;
        _cwd = cwd ?? Directory.GetCurrentDirectory();
    }

    public bool Detect()
    {
        foreach (var file in DocFiles)
            if (File.Exists(Path.Combine(_cwd, file))) return true;
        foreach (var dir in DocDirs)
            if (Directory.Exists(Path.Combine(_cwd, dir))) return true;
        return false;
    }

    public ImporterScanResult Scan()
    {
        var knowledgeFiles = 0;
        foreach (var file in DocFiles)
        {
            if (File.Exists(Path.Combine(_cwd, file))) knowledgeFiles++;
        }
        foreach (var dir in DocDirs)
        {
            var dirPath = Path.Combine(_cwd, dir);
            if (Directory.Exists(dirPath))
                knowledgeFiles += CountMarkdownFiles(dirPath);
        }
        return new ImporterScanResult(0, knowledgeFiles, 0);
    }

    /// <summary>Project docs have no per-project memories.</summary>
    public ImportResult ImportMemories(string? project = null) => ImportResult.Empty;

    public ImportResult ImportKnowledge()
    {
        var trimmedCwd = _cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseName = Path.GetFileName(trimmedCwd);
        if (string.IsNullOrEmpty(baseName)) baseName = trimmedCwd;
        var collectionName = $"{baseName}-project-docs";

        var files = new List<string>();
        foreach (var file in DocFiles)
        {
            var p = Path.Combine(_cwd, file);
            if (File.Exists(p)) files.Add(p);
        }
        foreach (var dir in DocDirs)
        {
            var dirPath = Path.Combine(_cwd, dir);
            if (Directory.Exists(dirPath))
                CollectMarkdownFiles(dirPath, files);
        }

        if (files.Count == 0) return ImportResult.Empty;

        // Reuse existing collection with the same name, or create a new one.
        var existing = _index.ListCollections().FirstOrDefault(c => c.Name == collectionName);
        var collectionId = existing is not null
            ? existing.Entry.Id
            : _index.CreateCollection(new CreateCollectionOpts(collectionName, _cwd));

        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var filePath in files)
        {
            string content;
            try
            {
                content = File.ReadAllText(filePath).Trim();
            }
            catch (Exception ex)
            {
                errors.Add($"{filePath}: {ex.Message}");
                continue;
            }

            if (content.Length == 0)
            {
                skipped++;
                continue;
            }

            var hash = ImportLog.ContentHash(content);
            if (_importLog.IsAlreadyImported(hash))
            {
                skipped++;
                continue;
            }

            try
            {
                _ = _ingester.IngestFile(filePath, collectionId);
                _importLog.LogImport(
                    Name,
                    filePath,
                    hash,
                    collectionId,
                    Tier.Cold,
                    ContentType.Knowledge);
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"{filePath}: {ex.Message}");
            }
        }

        return new ImportResult(imported, skipped, errors);
    }

    // --- helpers ---------------------------------------------------------

    private static void CollectMarkdownFiles(string dirPath, List<string> files)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dirPath);
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    CollectMarkdownFiles(entry, files);
                }
                else if (entry.EndsWith(".md", StringComparison.Ordinal))
                {
                    files.Add(entry);
                }
            }
            catch
            {
                // skip unreadable
            }
        }
    }

    private static int CountMarkdownFiles(string dirPath)
    {
        var list = new List<string>();
        CollectMarkdownFiles(dirPath, list);
        return list.Count;
    }
}
