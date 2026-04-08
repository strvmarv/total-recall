using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Outcome of a single <see cref="ImportUtils.ImportMarkdownFile"/> call.
/// </summary>
public enum ImportFileStatus
{
    Imported,
    Skipped,
    Errored,
}

/// <summary>
/// Result of <see cref="ImportUtils.ImportMarkdownFile"/>. For
/// <see cref="ImportFileStatus.Errored"/>, <see cref="Error"/> carries a
/// human-readable message in the form <c>"{filePath}: {ex.Message}"</c>.
/// </summary>
public sealed record ImportFileOutcome(
    ImportFileStatus Status,
    string? Error = null);

/// <summary>
/// Parsed frontmatter block from a markdown file. Mirrors the TS
/// <c>Frontmatter</c> interface in <c>src-ts/importers/import-utils.ts</c>.
/// All fields are optional because importers parse a wide variety of files.
/// </summary>
public sealed record Frontmatter(
    string? Name = null,
    string? Description = null,
    string? Type = null);

/// <summary>
/// Result of <see cref="ImportUtils.ParseFrontmatter"/>: the parsed
/// frontmatter (or <c>null</c> if the file had no <c>---</c> header) and
/// the content body that follows.
/// </summary>
public sealed record FrontmatterParseResult(
    Frontmatter? Frontmatter,
    string Content);

/// <summary>
/// Static helpers shared by the 7 host importers. Ports the subset of
/// <c>src-ts/importers/import-utils.ts</c> that isn't already covered by
/// <c>TotalRecall.Infrastructure.Telemetry.ImportLog</c> (which owns the
/// content-hash / dedupe / log-write helpers from the TS module).
/// </summary>
public static class ImportUtils
{
    private const string Marker = "---\n";

    /// <summary>
    /// Parse a markdown file's YAML-ish frontmatter block. Mirrors the TS
    /// <c>parseFrontmatter</c> regex
    /// <c>/^---\n([\s\S]*?)\n---\n([\s\S]*)$/</c> — only recognizes
    /// <c>name</c>, <c>description</c>, and <c>type</c> keys; ignores
    /// everything else. CRLF line endings are normalized to LF before
    /// parsing, matching the TS behaviour.
    /// </summary>
    public static FrontmatterParseResult ParseFrontmatter(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var normalised = raw.Replace("\r\n", "\n");

        if (!normalised.StartsWith(Marker, StringComparison.Ordinal))
        {
            return new FrontmatterParseResult(null, normalised);
        }

        var bodyStart = Marker.Length;
        // TS regex requires \n---\n as the closing delimiter.
        var closing = normalised.IndexOf("\n" + Marker, bodyStart, StringComparison.Ordinal);
        if (closing < 0)
        {
            return new FrontmatterParseResult(null, normalised);
        }

        var body = normalised.Substring(bodyStart, closing - bodyStart);
        var content = normalised.Substring(closing + 1 + Marker.Length);

        string? name = null;
        string? description = null;
        string? type = null;

        foreach (var line in body.Split('\n'))
        {
            // TS line regex: /^(\w+):\s*(.*)$/
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line.Substring(0, colonIdx);
            if (!IsWordChars(key)) continue;
            // \s* after the colon then everything until end-of-line, then
            // .trim() in TS.
            var value = line.Substring(colonIdx + 1).Trim();
            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "type": type = value; break;
                // Other keys are silently ignored, matching the TS (the
                // TS assigns them but the Frontmatter interface only
                // exposes the three keys we handle here).
            }
        }

        return new FrontmatterParseResult(new Frontmatter(name, description, type), content);
    }

    /// <summary>
    /// Standard markdown-import pipeline shared across host importers:
    /// read file → hash → dedupe check → (optionally) parse frontmatter →
    /// insert entry → embed → insert vector → log import row.
    ///
    /// Returns one of:
    /// <list type="bullet">
    ///   <item><see cref="ImportFileStatus.Imported"/> on success.</item>
    ///   <item><see cref="ImportFileStatus.Skipped"/> if the content hash
    ///     already exists in <c>import_log</c>.</item>
    ///   <item><see cref="ImportFileStatus.Errored"/> with an error message
    ///     on any exception during read/parse/insert/embed/log.</item>
    /// </list>
    ///
    /// Concrete importers compose this with their per-importer routing
    /// logic (tier/type, base tag selection, source-tool DU).
    ///
    /// When <paramref name="parseFrontmatter"/> is false (Copilot CLI plan.md,
    /// Cline global rules), the file content is inserted verbatim and
    /// <paramref name="prependFrontmatterName"/> has no effect.
    /// </summary>
    public static ImportFileOutcome ImportMarkdownFile(
        ISqliteStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        ImportLog importLog,
        string sourceToolName,
        SourceTool sourceToolDu,
        string filePath,
        Tier tier,
        ContentType contentType,
        IReadOnlyList<string> baseTags,
        bool prependFrontmatterName,
        bool parseFrontmatter = true,
        string? project = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(importLog);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(baseTags);

        try
        {
            var raw = File.ReadAllText(filePath);
            var hash = ImportLog.ContentHash(raw);

            if (importLog.IsAlreadyImported(hash))
            {
                return new ImportFileOutcome(ImportFileStatus.Skipped);
            }

            string content;
            string? summary;
            IReadOnlyList<string> tags;

            if (parseFrontmatter)
            {
                var parsed = ParseFrontmatter(raw);
                content = parsed.Content;
                // When prependFrontmatterName is false the caller has
                // opted out of frontmatter-driven metadata entirely —
                // fixed tags, no summary from description. This matches
                // the TS "only destructure content" pattern used by
                // ClaudeCode knowledge and OpenCode global AGENTS.md.
                if (prependFrontmatterName)
                {
                    summary = parsed.Frontmatter?.Description;
                    tags = !string.IsNullOrEmpty(parsed.Frontmatter?.Name)
                        ? new[] { parsed.Frontmatter!.Name! }.Concat(baseTags).ToArray()
                        : baseTags.ToArray();
                }
                else
                {
                    summary = null;
                    tags = baseTags.ToArray();
                }
            }
            else
            {
                content = raw;
                summary = null;
                tags = baseTags.ToArray();
            }

            var entryId = store.Insert(tier, contentType, new InsertEntryOpts(
                Content: content,
                Summary: summary,
                Source: filePath,
                SourceTool: sourceToolDu,
                Project: project,
                Tags: tags));

            var embedding = embedder.Embed(content);
            vectorSearch.InsertEmbedding(tier, contentType, entryId, embedding);
            importLog.LogImport(sourceToolName, filePath, hash, entryId, tier, contentType);

            return new ImportFileOutcome(ImportFileStatus.Imported);
        }
        catch (Exception ex)
        {
            return new ImportFileOutcome(
                ImportFileStatus.Errored,
                $"{filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the running import counters based on an <see cref="ImportFileOutcome"/>.
    /// Small helper to DRY up the aggregation switch at call sites.
    /// </summary>
    public static void Tally(
        ImportFileOutcome outcome,
        ref int imported,
        ref int skipped,
        List<string> errors)
    {
        switch (outcome.Status)
        {
            case ImportFileStatus.Imported: imported++; break;
            case ImportFileStatus.Skipped: skipped++; break;
            case ImportFileStatus.Errored: errors.Add(outcome.Error!); break;
        }
    }

    /// <summary>\w in JS regex: letters, digits, underscore.</summary>
    private static bool IsWordChars(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }
}
