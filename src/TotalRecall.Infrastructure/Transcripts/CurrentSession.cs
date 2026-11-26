namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>How the current session was found: by a marker, by the bounded guess, or not at all.</summary>
public enum CurrentKind
{
    None,
    Marker,
    Guessed,
}

/// <summary>
/// The session a host process is running (spec section 2). <see cref="Wire"/> is the word the tools
/// answer with (<c>currentSession</c>); <see cref="Ref"/> is the <c>&lt;host&gt;:&lt;id&gt;</c> form of a session
/// reference, or null when there is no session.
/// </summary>
public sealed record CurrentSession(CurrentKind Kind, string? Host, string? HostSessionId, string? TranscriptPath, string? Cwd)
{
    public static CurrentSession None { get; } = new(CurrentKind.None, null, null, null, null);

    public string Wire => Kind switch
    {
        CurrentKind.Marker => "marker",
        CurrentKind.Guessed => "guessed",
        _ => "none",
    };

    public string? Ref => Host is not null && HostSessionId is not null ? Host + ":" + HostSessionId : null;
}
