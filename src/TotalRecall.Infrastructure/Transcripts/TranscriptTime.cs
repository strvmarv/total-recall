using System;
using System.Globalization;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The one timestamp format of the archive (<c>yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'</c>, UTC) and the lenient parse of it.
/// </summary>
internal static class TranscriptTime
{
    public static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            return true;
        value = default;
        return false;
    }
}
