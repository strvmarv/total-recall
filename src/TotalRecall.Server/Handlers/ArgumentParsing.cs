// src/TotalRecall.Server/Handlers/ArgumentParsing.cs
//
// Shared argument-parsing helpers for MCP tool handlers. Houses coercion
// logic that multiple handlers need — e.g. tags, which MCP clients sometimes
// serialize as a JSON-encoded string rather than a native array.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TotalRecall.Server.Handlers;

internal static class ArgumentParsing
{
    /// <summary>
    /// Read the <c>tags</c> property from an MCP arguments object.
    /// Accepts any of:
    /// <list type="bullet">
    ///   <item>A native JSON array of strings: <c>["a","b"]</c></item>
    ///   <item>A JSON-encoded array string: <c>"[\"a\",\"b\"]"</c></item>
    ///   <item>A comma-separated string: <c>"a, b, c"</c></item>
    /// </list>
    /// Returns <c>null</c> when the property is absent or null.
    /// </summary>
    public static IReadOnlyList<string>? ReadTags(JsonElement args)
    {
        if (!args.TryGetProperty("tags", out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;

        // Native array — the happy path.
        if (prop.ValueKind == JsonValueKind.Array)
            return ParseStringArray(prop);

        // String — try JSON-parse first, fall back to comma-split.
        if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Attempt JSON array parse (handles '["a","b"]' serialized as a string).
            if (raw.TrimStart().StartsWith('['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        return ParseStringArray(doc.RootElement);
                }
                catch (JsonException)
                {
                    // Not valid JSON — fall through to comma-split.
                }
            }

            // Comma-separated: "git, commits, preference"
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length > 0 ? parts : null;
        }

        throw new ArgumentException("tags must be an array or a comma-separated string");
    }

    private static List<string> ParseStringArray(JsonElement arrayElement)
    {
        var list = new List<string>(arrayElement.GetArrayLength());
        var i = 0;
        foreach (var el in arrayElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"tags[{i}] must be a string");
            list.Add(el.GetString() ?? throw new ArgumentException($"tags[{i}] must be a string"));
            i++;
        }
        return list;
    }
}
