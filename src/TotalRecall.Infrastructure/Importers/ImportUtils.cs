using System;

namespace TotalRecall.Infrastructure.Importers;

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
