using System;
using System.Text.RegularExpressions;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Decides whether a search query looks like an error string (search the archive, spec section 5 item 3), which turns on
/// the boost of chunks that contain an error. The rules are deliberately loose and untuned, and they look at the query's
/// shape, not its intent: <c>error handling strategy discussion</c> counts (it contains <c>error</c>), as does a lone
/// <c>HTTP</c> (an all-caps word of four letters).
/// </summary>
public static class ErrorQueryDetector
{
    private static readonly string[] Keywords = { "error", "exception", "failed", "fatal", "panic", "traceback" };

    // exit 137, exit code 1, status 404, status code 500, HTTP 500, HTTP/1.1 500 is not matched on purpose
    private static readonly Regex StatusNumber = new(
        @"\b(?:exit|status|http)\s+(?:code\s+)?\d{1,3}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A Xxx.YyyException or ...Error name (.NET, Python) contains the keyword, so the keyword rule covers it.
    public static bool LooksLikeError(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        foreach (var keyword in Keywords)
        {
            if (query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (StatusNumber.IsMatch(query))
            return true;

        return HasErrorCode(query);
    }

    /// <summary>An all-caps token (letters, digits, underscore) with an underscore, or with at least four letters.</summary>
    private static bool HasErrorCode(string query)
    {
        var i = 0;
        while (i < query.Length)
        {
            while (i < query.Length && !IsTokenChar(query[i])) i++;
            var start = i;
            while (i < query.Length && IsTokenChar(query[i])) i++;
            if (i == start) break;

            var letters = 0;
            var underscore = false;
            var allCaps = true;
            for (var j = start; j < i; j++)
            {
                var c = query[j];
                if (c == '_') underscore = true;
                else if (c is >= 'A' and <= 'Z') letters++;
                else if (c is >= 'a' and <= 'z') { allCaps = false; break; }
            }

            if (allCaps && letters >= 2 && (underscore || letters >= 4))
                return true;
        }
        return false;
    }

    private static bool IsTokenChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
}
