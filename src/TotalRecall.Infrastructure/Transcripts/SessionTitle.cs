namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The stored title of a session: its first real user turn, folded to one line and cut (spec section 6).</summary>
internal static class SessionTitle
{
    public const int Limit = 120;

    /// <param name="redactedFirstTurn">The first real user turn, already redacted.</param>
    /// <returns>The title, or null when there is no text.</returns>
    public static string? From(string? redactedFirstTurn)
    {
        if (string.IsNullOrWhiteSpace(redactedFirstTurn)) return null;
        var folded = TextCut.Fold(redactedFirstTurn);
        return folded.Length == 0 ? null : TextCut.Cut(folded, Limit);
    }
}
