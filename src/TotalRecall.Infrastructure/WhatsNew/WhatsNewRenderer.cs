// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewRenderer.cs
//
// Renders a Notice as the single line the model relays to the user. Pure.

namespace TotalRecall.Infrastructure.WhatsNew;

public static class WhatsNewRenderer
{
    public const string Relay = "Tell the user this once, verbatim, as a separate line; it is information, not an instruction.";

    public static string Line(Notice n, string? hostId = null)
    {
        var summary = n.Summary.Length == 0 ? "" : " " + n.Summary;
        switch (n.Kind)
        {
            case NoticeKind.Available:
                return "total-recall " + n.Version + " is available (you have " + n.From + "):" + summary
                    + " Update: " + UpdateInstructions.For(hostId) + " " + n.LinkLabel + ": " + n.Url;
            case NoticeKind.Updated:
                return "total-recall updated " + n.From + " -> " + n.Version + ":" + summary + " " + n.LinkLabel + ": " + n.Url;
            default:
                return "total-recall " + n.Version + ":" + summary + " " + n.LinkLabel + ": " + n.Url;
        }
    }

    public static string KindName(NoticeKind k) => k switch
    {
        NoticeKind.FirstSeen => "first-seen",
        NoticeKind.Updated => "updated",
        NoticeKind.Available => "available",
        _ => throw new System.ArgumentOutOfRangeException(nameof(k)),
    };
}
