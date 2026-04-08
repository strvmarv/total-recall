// src/TotalRecall.Infrastructure/Json/JsonWriter.cs
//
// Plan 5 Task 5.10 — shared hand-rolled JSON emission helpers. Replaces
// ~10 duplicated AppendString/EscapeForJson helpers across
// Eval/Memory/Kb CLI commands and ConfigJsonSerializer. The helpers are
// AOT-safe (no reflection, no source-gen) and follow RFC 8259 string
// escaping. Numeric formatting uses invariant culture with the "R"
// round-trip specifier for doubles to preserve dedup semantics.
//
// Intentionally minimal: only helpers that are actually duplicated across
// ≥3 call sites live here. Per-file object/array layout stays per-file;
// different commands have different shapes and we do not try to factor
// "emit an entry DTO" across them.

using System.Globalization;
using System.Text;

namespace TotalRecall.Infrastructure.Json;

/// <summary>
/// Hand-rolled, AOT-safe JSON emission helpers. All methods append to
/// a caller-provided <see cref="StringBuilder"/>; they never allocate
/// intermediate strings beyond culture formatting.
/// </summary>
public static class JsonWriter
{
    /// <summary>
    /// Append a JSON string value including the surrounding quotes and
    /// all escape sequences required by RFC 8259 (quote, backslash,
    /// control characters, \b, \f, \n, \r, \t).
    /// </summary>
    public static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
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
        sb.Append('"');
    }

    /// <summary>Append a JSON double literal using invariant culture + "R" specifier.</summary>
    public static void AppendNumber(StringBuilder sb, double value)
    {
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Append a JSON integer literal using invariant culture.</summary>
    public static void AppendNumber(StringBuilder sb, long value)
    {
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Append a JSON integer literal using invariant culture.</summary>
    public static void AppendNumber(StringBuilder sb, int value)
    {
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Append the literal <c>true</c> or <c>false</c>.</summary>
    public static void AppendBool(StringBuilder sb, bool value)
    {
        sb.Append(value ? "true" : "false");
    }

    /// <summary>Append the literal <c>null</c>.</summary>
    public static void AppendNull(StringBuilder sb)
    {
        sb.Append("null");
    }
}
