using System;
using System.Globalization;
using System.Text;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The one place a stored text is cut to a limit (spec section 4 "Cutting", section 3 "Characters"). Lengths are UTF-16
/// code units. A cut never ends inside an exact <c>[redacted:&lt;category&gt;]</c> marker (it moves before the marker, so
/// a redacted secret cannot leave a prefix behind) and never splits a surrogate pair.
/// </summary>
internal static class TextCut
{
    public const string Ellipsis = "...";

    // The exact markers SensitiveContentScanner.Redact writes. Text that only looks like a marker is plain text.
    private static readonly string[] Markers =
    {
        "[redacted:token]",
        "[redacted:private-key]",
        "[redacted:bearer]",
        "[redacted:url-credentials]",
        "[redacted:secret-assignment]",
        "[redacted:unprocessable]",
    };

    private static readonly int LongestMarker = MaxLength(Markers);

    /// <summary>
    /// The largest <c>n &lt;= limit</c> (and <c>&lt;= text.Length</c>) such that <c>text[..n]</c> does not end inside a
    /// marker or between the two halves of a surrogate pair.
    /// </summary>
    public static int SafeLength(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (limit <= 0) return 0;
        if (limit >= text.Length) return text.Length;

        var n = limit;

        // A marker holds no '[' after its first character, so at most one marker can straddle n: the one whose '['
        // is the last '[' in the window before n.
        var windowStart = Math.Max(0, n - LongestMarker + 1);
        var open = text.LastIndexOf('[', n - 1, n - windowStart);
        if (open >= 0)
        {
            foreach (var marker in Markers)
            {
                if (open + marker.Length > n && open + marker.Length <= text.Length
                    && string.CompareOrdinal(text, open, marker, 0, marker.Length) == 0)
                {
                    n = open;
                    break;
                }
            }
        }

        if (n > 0 && n < text.Length && char.IsHighSurrogate(text[n - 1]) && char.IsLowSurrogate(text[n])) n--;
        return n;
    }

    /// <summary><paramref name="text"/> when it fits, else its safe prefix followed by <see cref="Ellipsis"/>.</summary>
    public static string Cut(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= limit) return text;
        return string.Concat(text.AsSpan(0, SafeLength(text, limit)), Ellipsis);
    }

    /// <summary>
    /// <paramref name="text"/> when it fits, else its safe prefix followed by
    /// <c> [... N more characters not stored]</c>, where N counts every character not kept.
    /// </summary>
    public static string Cap(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= limit) return text;
        var n = SafeLength(text, limit);
        return string.Concat(
            text.AsSpan(0, n),
            " [... ",
            (text.Length - n).ToString(CultureInfo.InvariantCulture),
            " more characters not stored]");
    }

    /// <summary>Newlines become spaces, every run of whitespace becomes one space, and the ends are trimmed.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int MaxLength(string[] values)
    {
        var max = 0;
        foreach (var v in values) max = Math.Max(max, v.Length);
        return max;
    }
}
