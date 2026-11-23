using System;
using System.Collections.Generic;
using System.Text;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The text of one exchange (spec section 2 "The text of an exchange"): the chunks of the exchange in ascending part,
/// each without its header line, joined with a blank line, minus the continuation prefix that the chunker repeats at the
/// start of every part after the first so a part is searchable on its own.
/// </summary>
internal static class ExchangeText
{
    private const string UserLabel = "User: ";

    /// <summary>
    /// <paramref name="partsAscending"/> are the chunks of one exchange ordered by part. From the second part on, the
    /// <see cref="TranscriptChunker.ContinuedLabel"/> and the repeated question (up to and including the first blank
    /// line) are removed, but only for a <c>dialogue</c> exchange whose first part body begins with <c>User: </c> and
    /// whose later part body begins with the label; any other text is kept as stored.
    /// </summary>
    public static string Join(IReadOnlyList<ChunkRow> partsAscending)
    {
        ArgumentNullException.ThrowIfNull(partsAscending);
        if (partsAscending.Count == 0) return "";

        var first = TranscriptSearchService.ChunkText(partsAscending[0].Content);
        var strip = partsAscending[0].ChunkType == TranscriptChunker.DialogueType
            && first.StartsWith(UserLabel, StringComparison.Ordinal);

        var text = new StringBuilder(first);
        for (var i = 1; i < partsAscending.Count; i++)
        {
            var body = TranscriptSearchService.ChunkText(partsAscending[i].Content);
            if (strip && body.StartsWith(TranscriptChunker.ContinuedLabel, StringComparison.Ordinal))
            {
                var blank = body.IndexOf(TranscriptChunker.Separator, StringComparison.Ordinal);
                if (blank >= 0) body = body[(blank + TranscriptChunker.Separator.Length)..];
            }

            text.Append(TranscriptChunker.Separator).Append(body);
        }

        return text.ToString();
    }

    /// <summary><c>ceil(length / 4)</c>: the token estimate a budget counts (spec call 3).</summary>
    public static int Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3) / 4;
    }
}
