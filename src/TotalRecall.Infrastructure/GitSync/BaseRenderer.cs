using System.Text;
using TotalRecall.Core;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// Renders the new <c>base_json</c> of a row after a pull (decision 18): per group, the record just read when the
/// decision's <see cref="SyncMerge.BaseUpdate"/> says the base becomes the record's, else the old base's. Unknown
/// fields and unknown <c>ext</c> namespaces of the record just read survive.
/// </summary>
internal static class BaseRenderer
{
    /// <summary>
    /// The UTF-8 text of the new base, or null when the record cannot be encoded or its rendering is over the 1 MiB a record file may be. With no old base every group is the
    /// record's. The content group is the text, entry type, source, project, scope, <c>created_at</c>, content stamp,
    /// <c>origin</c> and <c>written_by</c>; the sticky group the flag and its stamp; the tier group the tier and its
    /// stamp; tags have no stamp. A sticky result is written hot.
    /// </summary>
    public static string? Render(ParsedRecord theirs, ParsedRecord? oldBase, SyncMerge.BaseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(update);
        var all = oldBase is null
            || (update.Content.IsBaseBecomesTheirs && update.Tags.IsBaseBecomesTheirs
                && update.Sticky.IsBaseBecomesTheirs && update.Tier.IsBaseBecomesTheirs);
        var written = all ? RecordWriter.Write(theirs) : RenderMixed(theirs, oldBase!, update);
        // A base the reader would reject as oversize could never be read back: fail the entry now, not on every later pull.
        if (written.Bytes is null || written.Bytes.Length > SyncValidation.maxFileBytes)
            return null;
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(written.Bytes);
    }

    private static WriteResult RenderMixed(ParsedRecord theirs, ParsedRecord oldBase, SyncMerge.BaseUpdate update)
    {
        var t = theirs.Record;
        var b = oldBase.Record;
        var content = update.Content.IsBaseBecomesTheirs ? t : b;
        var sticky = update.Sticky.IsBaseBecomesTheirs ? t : b;
        var tier = update.Tier.IsBaseBecomesTheirs ? t : b;
        var tags = update.Tags.IsBaseBecomesTheirs ? t.Tags : b.Tags;

        var isSticky = sticky.Placement.Sticky;
        var tierRecord = update.Tier.IsBaseBecomesTheirs ? theirs : oldBase;
        var writtenTier = isSticky ? Tier.Hot : tierRecord.StoredTier;
        var placement = new SyncRecordModule.Placement(
            isSticky ? Tier.Hot : tier.Placement.Tier, tier.Placement.TierStamp, isSticky, sticky.Placement.StickyStamp);

        var next = new SyncRecord(
            t.Id, content.Content, content.EntryType, tags, content.Source, content.CreatedAtMs, content.Project, content.Scope,
            placement, content.ContentStamp, content.Origin, content.WrittenBy);

        var unknownFrom = update.Content.IsBaseBecomesTheirs || update.Tags.IsBaseBecomesTheirs
            || update.Sticky.IsBaseBecomesTheirs || update.Tier.IsBaseBecomesTheirs
            ? theirs
            : oldBase;
        var unknownSource = update.Content.IsBaseBecomesTheirs ? theirs.UnknownSource : oldBase.UnknownSource;
        var contentFrom = update.Content.IsBaseBecomesTheirs ? theirs : oldBase;
        var u = unknownFrom.Unknown;
        var unknown = new UnknownContent(u.Top, u.ExtNamespaces, u.TotalRecall, u.Placement, contentFrom.Unknown.Origin);
        return RecordWriter.WriteCore(next, writtenTier, unknown, unknownSource);
    }
}
