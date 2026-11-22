using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The fields of a memory the secret check reads (#161, decision D2): the text, the project, the scope and each tag. One
/// place for the scan and the push, so they cannot disagree about which fields count as a hit.
/// </summary>
internal static class SecretFields
{
    /// <summary>
    /// Whether <paramref name="detector"/> finds anything (a non-empty result) in the text, the project, the scope or any
    /// tag of <paramref name="view"/>. An absent project or scope is not passed to the detector.
    /// </summary>
    public static bool AnyHit(SyncMerge.LocalEntry view, Func<string?, IReadOnlyList<string>> detector)
    {
        var content = view.Content;
        if (Finds(detector, content.Text) || Finds(detector, content.Project?.Value) || Finds(detector, content.Scope?.Value))
            return true;
        foreach (var tag in view.Tags)
            if (Finds(detector, tag))
                return true;
        return false;
    }

    private static bool Finds(Func<string?, IReadOnlyList<string>> detector, string? text) =>
        text is not null && detector(text).Count > 0;
}
