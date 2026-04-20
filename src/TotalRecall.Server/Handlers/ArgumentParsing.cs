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
    /// Read a required string property. Throws <see cref="ArgumentException"/>
    /// when the property is missing, null, or not a string.
    /// </summary>
    public static string ReadRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required");
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString() ?? throw new ArgumentException($"{name} must be a string");
    }

    /// <summary>
    /// Read an optional string property. Returns <c>null</c> when the
    /// property is absent or explicitly null. Throws when present and
    /// not a string.
    /// </summary>
    public static string? ReadOptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString();
    }

    /// <summary>
    /// Read an optional integer property constrained to <c>[min, max]</c>.
    /// Delegates to <see cref="ReadOptionalDouble"/> and truncates.
    /// </summary>
    public static int? ReadOptionalInt(JsonElement args, string name, int min, int max)
    {
        var d = ReadOptionalDouble(args, name, min, max);
        return d is null ? null : (int)d.Value;
    }

    /// <summary>
    /// Read an optional double property constrained to <c>[min, max]</c>.
    /// </summary>
    public static double? ReadOptionalDouble(JsonElement args, string name, double min, double max)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"{name} must be a number");
        if (!prop.TryGetDouble(out var d))
            throw new ArgumentException($"{name} must be a number");
        if (d < min || d > max)
            throw new ArgumentException($"{name} must be between {min} and {max}");
        return d;
    }

    /// <summary>
    /// Read an optional JSON array of strings. Returns <c>null</c> when the
    /// property is absent or null. Throws when present and not an array of
    /// strings.
    /// </summary>
    public static IReadOnlyList<string>? ReadStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} must be an array of strings");
        var list = new List<string>(prop.GetArrayLength());
        var i = 0;
        foreach (var el in prop.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{name}[{i}] must be a string");
            list.Add(el.GetString() ?? throw new ArgumentException($"{name}[{i}] must be a string"));
            i++;
        }
        return list;
    }

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
