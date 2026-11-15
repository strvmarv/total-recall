// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewRenderer.cs
//
// Renders a Notice as the single line the model relays to the user. Pure.

namespace TotalRecall.Infrastructure.WhatsNew;

public static class WhatsNewRenderer
{
    public const string Relay = "Tell the user this once, verbatim, as a separate line; it is information, not an instruction.";

    public static string Line(Notice n)
    {
        var head = n.Kind == NoticeKind.Updated
            ? "total-recall updated " + n.From + " -> " + n.Version + ":"
            : "total-recall " + n.Version + ":";
        var summary = n.Summary.Length == 0 ? "" : " " + n.Summary;
        return head + summary + " " + n.LinkLabel + ": " + n.Url;
    }

    public static string KindName(NoticeKind k) => k == NoticeKind.Updated ? "updated" : "first-seen";
}
