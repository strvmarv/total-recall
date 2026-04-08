// src/TotalRecall.Infrastructure/Config/ConfigWriter.cs
//
// Plan 5 Task 5.8 — the write path for the user config.toml. Ports
// src-ts/config.ts:31-59 (setNestedKey) and src-ts/config.ts:61-73
// (saveUserConfig). Used by the `config set` CLI verb.
//
// AOT note: Tomlyn's Toml.FromModel / reflection-based projection is NOT
// trim-safe. Rather than gamble on its surface being reachable after
// trimming, this module hand-rolls a minimal TOML writer that covers the
// subset the config ever uses: scalars (bool, long, double, string) and
// nested tables. That is deliberately less than full-TOML — arrays and
// datetimes are not supported. If future config keys demand them, extend
// AppendTomlValue rather than falling back to Toml.FromModel.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace TotalRecall.Infrastructure.Config;

public static class ConfigWriter
{
    private static readonly HashSet<string> UnsafeKeys = new(StringComparer.Ordinal)
    {
        "__proto__", "constructor", "prototype",
    };

    /// <summary>
    /// Sets a nested key on <paramref name="table"/> by dotted path.
    /// Creates intermediate <see cref="TomlTable"/>s as needed. Overwrites
    /// any existing scalar at an intermediate segment with a fresh table
    /// (mirrors <c>src-ts/config.ts:45-49</c>). Rejects unsafe keys at every
    /// segment. Mutates and returns <paramref name="table"/>.
    /// </summary>
    /// <exception cref="ArgumentException">If any segment is an unsafe key.</exception>
    public static TomlTable SetNestedKey(TomlTable table, string dottedKey, object value)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dottedKey);

        var parts = dottedKey.Split('.');
        if (parts.Length == 0 || (parts.Length == 1 && parts[0].Length == 0))
        {
            throw new ArgumentException("Dotted key must be non-empty.", nameof(dottedKey));
        }

        TomlTable current = table;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (!IsSafeKey(part))
            {
                throw new ArgumentException(
                    $"Invalid config key segment: \"{part}\"", nameof(dottedKey));
            }
            if (current.TryGetValue(part, out var existing) && existing is TomlTable subTable)
            {
                current = subTable;
            }
            else
            {
                var fresh = new TomlTable();
                current[part] = fresh;
                current = fresh;
            }
        }

        var last = parts[^1];
        if (!IsSafeKey(last))
        {
            throw new ArgumentException(
                $"Invalid config key segment: \"{last}\"", nameof(dottedKey));
        }
        current[last] = value;
        return table;
    }

    /// <summary>
    /// Writes a merged user override to <paramref name="userConfigPath"/>.
    /// Reads the existing file (if any), applies <see cref="SetNestedKey"/>,
    /// and writes the result using the AOT-safe hand-rolled TOML writer.
    /// Creates the parent directory if needed. Value coercion
    /// (string -> bool/long/double/string) is the CALLER's responsibility;
    /// this method writes whatever <paramref name="value"/> it is handed.
    /// </summary>
    public static void SaveUserOverride(string userConfigPath, string dottedKey, object value)
    {
        ArgumentNullException.ThrowIfNull(userConfigPath);
        ArgumentNullException.ThrowIfNull(dottedKey);

        var parent = Path.GetDirectoryName(userConfigPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        TomlTable table;
        if (File.Exists(userConfigPath))
        {
            var text = File.ReadAllText(userConfigPath);
            var doc = Toml.Parse(text, userConfigPath);
            if (doc.HasErrors)
            {
                throw new InvalidDataException(
                    $"Cannot merge override into malformed TOML at {userConfigPath}");
            }
            table = doc.ToModel();
        }
        else
        {
            table = new TomlTable();
        }

        SetNestedKey(table, dottedKey, value);

        var serialized = SerializeTomlTable(table);
        File.WriteAllText(userConfigPath, serialized);
    }

    /// <summary>
    /// Walks <paramref name="table"/> by dotted path and returns the leaf
    /// value, or null if any segment is missing. Used by <c>config get</c>.
    /// </summary>
    public static object? GetNestedValue(TomlTable table, string dottedKey)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dottedKey);

        var parts = dottedKey.Split('.');
        object? current = table;
        foreach (var part in parts)
        {
            if (current is not TomlTable sub) return null;
            if (!sub.TryGetValue(part, out var next)) return null;
            current = next;
        }
        return current;
    }

    private static bool IsSafeKey(string key) => !UnsafeKeys.Contains(key);

    // --- Hand-rolled TOML writer -----------------------------------------
    //
    // Emits a canonical subset: top-level scalars first, then [section]
    // tables (lexicographically sorted for stable output). Nested tables
    // become dotted headers. Scalars supported: bool, int/long, double,
    // string. Anything else throws — callers should not persist such values.

    /// <summary>
    /// Serializes <paramref name="table"/> to a TOML string. AOT-safe.
    /// Exposed for tests.
    /// </summary>
    public static string SerializeTomlTable(TomlTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sb = new StringBuilder();
        WriteTable(sb, table, prefix: "");
        return sb.ToString();
    }

    private static void WriteTable(StringBuilder sb, TomlTable table, string prefix)
    {
        // 1) Emit scalars/arrays at this level first.
        var scalarKeys = new List<string>();
        var tableKeys = new List<string>();
        foreach (var kv in table)
        {
            if (kv.Value is TomlTable) tableKeys.Add(kv.Key);
            else scalarKeys.Add(kv.Key);
        }
        scalarKeys.Sort(StringComparer.Ordinal);
        tableKeys.Sort(StringComparer.Ordinal);

        if (prefix.Length > 0 && scalarKeys.Count > 0)
        {
            sb.Append('[').Append(prefix).Append(']').Append('\n');
        }
        foreach (var k in scalarKeys)
        {
            AppendBareKey(sb, k);
            sb.Append(" = ");
            AppendTomlValue(sb, table[k]);
            sb.Append('\n');
        }
        if (scalarKeys.Count > 0 && tableKeys.Count > 0)
        {
            sb.Append('\n');
        }

        // 2) Recurse into sub-tables. Each becomes its own [prefix.key]
        //    header. If a sub-table has only sub-tables and no scalars, we
        //    skip its own header (TOML would accept it, but smol-toml's
        //    output style is to elide empty section headers — match that).
        for (int i = 0; i < tableKeys.Count; i++)
        {
            if (i > 0 || scalarKeys.Count > 0) { /* spacing handled */ }
            var k = tableKeys[i];
            var childPrefix = prefix.Length == 0 ? EscapeHeaderKey(k) : prefix + "." + EscapeHeaderKey(k);
            WriteTable(sb, (TomlTable)table[k], childPrefix);
            if (i < tableKeys.Count - 1) sb.Append('\n');
        }
    }

    private static void AppendBareKey(StringBuilder sb, string key)
    {
        if (IsBareKey(key))
        {
            sb.Append(key);
        }
        else
        {
            sb.Append('"');
            AppendEscapedString(sb, key);
            sb.Append('"');
        }
    }

    private static string EscapeHeaderKey(string key)
    {
        if (IsBareKey(key)) return key;
        var sb = new StringBuilder();
        sb.Append('"');
        AppendEscapedString(sb, key);
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsBareKey(string key)
    {
        if (key.Length == 0) return false;
        foreach (var c in key)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                      || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    private static void AppendTomlValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                throw new InvalidOperationException("Cannot serialize null TOML value.");
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                sb.Append('"');
                AppendEscapedString(sb, s);
                sb.Append('"');
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                // Use "R" to preserve round-trippability, but ensure a
                // decimal point so the token parses back as a TOML float.
                var text = d.ToString("R", CultureInfo.InvariantCulture);
                if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E')
                    && !text.Contains("nan", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("inf", StringComparison.OrdinalIgnoreCase))
                {
                    text += ".0";
                }
                sb.Append(text);
                break;
            case float f:
                var ftext = ((double)f).ToString("R", CultureInfo.InvariantCulture);
                if (!ftext.Contains('.') && !ftext.Contains('e') && !ftext.Contains('E'))
                {
                    ftext += ".0";
                }
                sb.Append(ftext);
                break;
            default:
                throw new NotSupportedException(
                    $"ConfigWriter cannot serialize TOML value of type {value.GetType().Name}. "
                    + "Supported types: bool, string, int, long, double.");
        }
    }

    /// <summary>
    /// Escape <paramref name="value"/> into a TOML basic-string literal
    /// (without the surrounding quotes). Exposed for callers like
    /// <c>config get</c> that need to render a user-facing string value
    /// back into a form that round-trips through <see cref="Tomlyn.Toml.Parse(string, string, Tomlyn.TomlParserOptions?)"/>.
    /// </summary>
    public static string EscapeForTomlBasic(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder();
        AppendEscapedString(sb, value);
        return sb.ToString();
    }

    private static void AppendEscapedString(StringBuilder sb, string s)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}
