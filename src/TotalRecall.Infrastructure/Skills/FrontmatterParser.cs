using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TotalRecall.Infrastructure.Skills;

public sealed record ParsedFrontmatter(string? Name, string? Description, string RawJson);

/// <summary>
/// Ported from cortex-side <c>TotalRecall.Cortex.Data.Importers.FrontmatterParser</c>.
/// Strips a UTF-8 BOM if present, then extracts a leading <c>---\n...\n---\n</c> YAML
/// frontmatter block and returns it as JSON plus the body below the fence.
/// Fails closed on malformed YAML: the exception propagates so callers can
/// log-and-skip the offending skill.
/// </summary>
public static class FrontmatterParser
{
    // YamlDotNet's reflection-based builders trip IL3050 under AOT analysis. The
    // plugin host isn't AOT-published (Infrastructure just sets IsAotCompatible for
    // consumers that might be); frontmatter parsing is reflection-bound by design
    // and the input shapes are tiny dictionaries. Suppress locally rather than
    // weakening the project-wide AOT posture.
    [UnconditionalSuppressMessage("AOT", "IL3050:Requires dynamic code",
        Justification = "Plugin Infrastructure does not publish AOT; YamlDotNet reflection is acceptable here.")]
    private static IDeserializer BuildYaml() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [UnconditionalSuppressMessage("AOT", "IL3050:Requires dynamic code",
        Justification = "Plugin Infrastructure does not publish AOT; YamlDotNet reflection is acceptable here.")]
    private static ISerializer BuildJsonSerializer() => new SerializerBuilder()
        .JsonCompatible()
        .Build();

    private static readonly IDeserializer _yaml = BuildYaml();
    private static readonly ISerializer _jsonSerializer = BuildJsonSerializer();

    public static (ParsedFrontmatter? Frontmatter, string Body) Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, raw);

        // Strip UTF-8 BOM if present — common on Windows-authored skill files.
        if (raw[0] == '\uFEFF') raw = raw.Substring(1);
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

        // Deliberately NOT catching here — malformed YAML between `---` fences is a
        // caller-observable error. The scanner turns the exception into a ScanError
        // and drops the skill. A totally absent frontmatter (no fence) returns above
        // with (null, raw) and is treated as "no frontmatter present", not an error.
        var obj = _yaml.Deserialize<Dictionary<string, object?>>(yamlText) ?? new();
        var json = _jsonSerializer.Serialize(obj);
        string? name = obj.TryGetValue("name", out var n) ? n?.ToString() : null;
        string? desc = obj.TryGetValue("description", out var d) ? d?.ToString() : null;
        return (new ParsedFrontmatter(name, desc, json), body);
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
