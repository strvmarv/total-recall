using System.Text;

namespace TotalRecall.Infrastructure.Skills;

public sealed record ParsedFrontmatter(string? Name, string? Description, string RawJson);

/// <summary>
/// Ported from cortex-side <c>TotalRecall.Cortex.Data.Importers.FrontmatterParser</c>.
/// Strips a UTF-8 BOM if present, then extracts a leading <c>---\n...\n---\n</c> YAML
/// frontmatter block and returns it as JSON plus the body below the fence.
/// Fails closed on malformed YAML: the exception propagates so callers can
/// log-and-skip the offending skill.
///
/// AOT-safe: uses a hand-rolled key:value parser rather than YamlDotNet reflection
/// (Dictionary&lt;string, object?&gt; deserialization is trimmed away in NativeAOT builds).
/// Supports the flat key:value format used by skill frontmatter; YAML block scalars
/// and anchors are not needed here.
/// </summary>
public static class FrontmatterParser
{
    public static (ParsedFrontmatter? Frontmatter, string Body) Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, raw);

        // Strip UTF-8 BOM if present — common on Windows-authored skill files.
        if (raw[0] == '﻿') raw = raw.Substring(1);
        if (string.IsNullOrEmpty(raw)) return (null, raw);

        // Must start with exactly "---" followed by newline.
        if (!raw.StartsWith("---", StringComparison.Ordinal))
            return (null, raw);

        var afterOpen = raw.AsSpan(3);
        int newlineIdx = afterOpen.IndexOfAny('\n', '\r');
        if (newlineIdx < 0) return (null, raw);
        int scanFrom = 3 + newlineIdx + 1;
        if (scanFrom < raw.Length && raw[scanFrom - 1] == '\r' && raw[scanFrom] == '\n') scanFrom++;

        // Find closing "---" on its own line.
        int closeIdx = FindClosingFence(raw, scanFrom);
        if (closeIdx < 0) return (null, raw);

        var yamlText = raw.Substring(scanFrom, closeIdx - scanFrom);
        int bodyStart = closeIdx + 3;
        // Skip trailing newline after closing fence.
        if (bodyStart < raw.Length && raw[bodyStart] == '\r') bodyStart++;
        if (bodyStart < raw.Length && raw[bodyStart] == '\n') bodyStart++;
        var body = bodyStart >= raw.Length ? string.Empty : raw.Substring(bodyStart);

        // Reflection-free flat YAML parse — handles the key: value format used by
        // skill frontmatter. Malformed input throws InvalidDataException which the
        // scanner catches and records as a ScanError.
        var obj = ParseFlatYaml(yamlText);
        var json = BuildJson(obj);
        string? name = obj.TryGetValue("name", out var n) ? n : null;
        string? desc = obj.TryGetValue("description", out var d) ? d : null;
        return (new ParsedFrontmatter(name, desc, json), body);
    }

    private static Dictionary<string, string> ParseFlatYaml(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = yaml.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');
            i++;

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                continue;

            var colonIdx = line.IndexOf(':');
            if (colonIdx < 1) continue;

            var key = line.Substring(0, colonIdx).Trim();
            if (string.IsNullOrEmpty(key)) continue;

            var valuePart = line.Substring(colonIdx + 1).Trim();

            // Block scalar indicator (| or >) — collect continuation lines.
            if (valuePart == "|" || valuePart == ">")
            {
                var sb = new StringBuilder();
                while (i < lines.Length)
                {
                    var cont = lines[i].TrimEnd('\r');
                    // Block scalar ends when a non-indented, non-empty line appears.
                    if (cont.Length > 0 && !char.IsWhiteSpace(cont[0])) break;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(cont.TrimStart());
                    i++;
                }
                result[key] = sb.ToString().Trim();
                continue;
            }

            // Strip surrounding quotes (single or double).
            var value = UnquoteScalar(valuePart);
            result[key] = value;
        }
        return result;
    }

    private static string UnquoteScalar(string s)
    {
        if (s.Length >= 2)
        {
            if (s[0] == '\'' && s[s.Length - 1] == '\'')
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            if (s[0] == '"' && s[s.Length - 1] == '"')
                return UnescapeDoubleQuoted(s.Substring(1, s.Length - 2));
        }
        return s;
    }

    private static string UnescapeDoubleQuoted(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[i + 1] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => s[i + 1]
                });
                i += 2;
            }
            else
            {
                sb.Append(s[i++]);
            }
        }
        return sb.ToString();
    }

    private static string BuildJson(Dictionary<string, string> obj)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in obj)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":\"").Append(JsonEscape(kv.Value)).Append('"');
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string JsonEscape(string s)
    {
        if (s.IndexOfAny(new[] { '"', '\\', '\n', '\r', '\t' }) < 0) return s;
        var sb = new StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static int FindClosingFence(string raw, int from)
    {
        int i = from;
        while (i < raw.Length)
        {
            // Line starts here; look for "---" followed by newline or EOF.
            if (i + 3 <= raw.Length && raw[i] == '-' && raw[i + 1] == '-' && raw[i + 2] == '-')
            {
                int after = i + 3;
                if (after == raw.Length || raw[after] == '\n' || raw[after] == '\r')
                    return i;
            }
            // Advance to next line start.
            int nl = raw.IndexOfAny(new[] { '\n', '\r' }, i);
            if (nl < 0) return -1;
            i = nl + 1;
            if (nl + 1 < raw.Length && raw[nl] == '\r' && raw[nl + 1] == '\n') i = nl + 2;
        }
        return -1;
    }
}
