using Microsoft.FSharp.Core;
using TotalRecall.Core;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>What <see cref="LocalEntryReader.Read"/> found for one memory id.</summary>
internal abstract record LocalRead
{
    private LocalRead() { }

    /// <summary>No memory with this id in any tier.</summary>
    public sealed record Absent : LocalRead;

    /// <summary>Exactly one readable entry.</summary>
    public sealed record Present(SyncMerge.LocalEntry View, Tier Tier) : LocalRead;

    /// <summary>The id is in more than one tier.</summary>
    public sealed record Duplicate : LocalRead;

    /// <summary>The entry exists but cannot be mapped (an unknown source tool, invalid tags, a NULL where the schema allows it).</summary>
    public sealed record Unreadable(string Reason) : LocalRead;
}

/// <summary>
/// The one mapping from a stored memory to the local view the merge decisions and the scan compare, and the
/// three-tier read the pull uses. The scan reads whole tables and calls <see cref="ToLocalEntry"/>; the pull reads
/// one id through <see cref="Read"/> (from the store, or from inside a store transaction).
/// </summary>
internal static class LocalEntryReader
{
    private static readonly Tier[] Tiers = [Tier.Hot, Tier.Warm, Tier.Cold];

    public static SyncMerge.LocalEntry ToLocalEntry(Entry e, Tier tier, bool sticky)
    {
        var content = new SyncMerge.ContentFields(
            e.Content,
            string.IsNullOrEmpty(OptionOrNull(e.Project)) ? null : e.Project,
            string.IsNullOrEmpty(e.Scope) ? null : FSharpOption<string>.Some(e.Scope),
            e.EntryType,
            e.SourceTool,
            e.CreatedAt);
        return new SyncMerge.LocalEntry(content, SyncRecordModule.normaliseTags(e.Tags), tier, sticky);
    }

    /// <summary>
    /// Reads one memory id over the hot, warm and cold tiers. <paramref name="get"/> returns the entry of a tier or
    /// null; a mapping failure it throws (<see cref="FormatException"/>, <see cref="ArgumentException"/>,
    /// <see cref="InvalidCastException"/>) means the row exists but cannot be read, and any other exception (a
    /// database error) propagates. <paramref name="isSticky"/> is asked only for a hot entry.
    /// </summary>
    public static LocalRead Read(Func<Tier, Entry?> get, Func<bool> isSticky)
    {
        var found = new List<(Tier Tier, Entry Entry)>();
        string? unreadable = null;
        var occurrences = 0;
        foreach (var tier in Tiers)
        {
            try
            {
                var entry = get(tier);
                if (entry is null)
                    continue;
                occurrences++;
                found.Add((tier, entry));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException)
            {
                occurrences++;
                unreadable ??= $"the entry cannot be read: {ex.Message}";
            }
        }

        if (occurrences == 0)
            return new LocalRead.Absent();
        if (occurrences > 1)
            return new LocalRead.Duplicate();
        if (unreadable is not null)
            return new LocalRead.Unreadable(unreadable);

        var (foundTier, foundEntry) = found[0];
        var sticky = foundTier.IsHot && isSticky();
        return new LocalRead.Present(ToLocalEntry(foundEntry, foundTier, sticky), foundTier);
    }

    /// <summary>The entry as the scan would compare it: every comparison column that is not NULL equals the entry's. A missing row or entry agrees.</summary>
    public static bool AgreesWithRow(GitSyncRow? row, LocalRead read)
    {
        if (row is null || read is not LocalRead.Present present)
            return true;
        var view = present.View;
        return (row.ScannedContentHash is null || row.ScannedContentHash == SyncScan.contentHash(view.Content))
            && (row.ScannedTagsHash is null || row.ScannedTagsHash == SyncScan.tagsHash(view.Tags))
            && (row.LocalSticky is null || row.LocalSticky == view.Sticky)
            && (row.LocalTier is null || row.LocalTier.Equals(view.Tier));
    }

    private static string? OptionOrNull(FSharpOption<string>? o) => o is null ? null : o.Value;
}
