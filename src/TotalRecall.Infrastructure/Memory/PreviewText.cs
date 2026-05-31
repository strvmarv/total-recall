// src/TotalRecall.Infrastructure/Memory/PreviewText.cs
//
// Shared single-line preview helper for the `recent` command (MCP handler +
// CLI). Collapses all whitespace runs to single spaces, trims, and truncates
// to a max length with a trailing ellipsis. No regex — keeps the AOT publish
// path reflection-free.

using System.Text;

namespace TotalRecall.Infrastructure.Memory;

public static class PreviewText
{
    /// <summary>
    /// Collapse internal whitespace to single spaces, trim ends, and truncate
    /// to <paramref name="max"/> characters (appending U+2026 when cut).
    /// Returns "" for null/empty input.
    /// </summary>
    public static string Collapse(string? content, int max)
    {
        if (string.IsNullOrEmpty(content)) return "";

        var sb = new StringBuilder(content.Length);
        var pendingSpace = false;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }

        var collapsed = sb.ToString();
        if (collapsed.Length <= max) return collapsed;
        return collapsed.Substring(0, max).TrimEnd() + "…";
    }
}
