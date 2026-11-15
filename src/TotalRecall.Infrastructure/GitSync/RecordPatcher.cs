using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

public enum ChangeKind
{
    /// <summary>Leave the property as the file has it. The default value of <see cref="Change{T}"/>.</summary>
    Keep,
    Set,
    Clear,
}

/// <summary>What to do with an optional property: keep it, set it, or remove it.</summary>
public readonly struct Change<T>
{
    private Change(ChangeKind kind, T? value)
    {
        Kind = kind;
        Value = value;
    }

    public ChangeKind Kind { get; }

    public T? Value { get; }

    public static Change<T> Keep => default;

    public static Change<T> Clear => new(ChangeKind.Clear, default);

    public static Change<T> Set(T value) => new(ChangeKind.Set, value);
}

/// <summary>A new content, entry type and content stamp, with changes to the optional properties. <c>updated_at</c> becomes the stamp's time.</summary>
public sealed record ContentPatch(
    string Content,
    EntryType EntryType,
    SyncStamp.Stamp ContentStamp,
    Change<string> Project = default,
    Change<string> Scope = default,
    Change<SourceTool> Source = default);

/// <summary>A pin or an unpin. <c>TierStampIfRaised</c> stamps the tier change a pin makes when the record is in a colder tier.</summary>
public sealed record StickyPatch(bool Sticky, SyncStamp.Stamp StickyStamp, SyncStamp.Stamp? TierStampIfRaised = null);

public sealed record TierPatch(Tier Tier, SyncStamp.Stamp TierStamp);

/// <summary>The groups to change; a null group is left exactly as the file has it.</summary>
public sealed record RecordPatch(
    ContentPatch? Content = null,
    IReadOnlyList<string>? Tags = null,
    StickyPatch? Sticky = null,
    TierPatch? Tier = null);

public enum PatchFailure
{
    None,
    NotEncodable,
    /// <summary>A pin that raises a colder record to hot came without a stamp for the tier change.</summary>
    MissingTierStamp,
}

/// <summary>The new file and the record it holds, or the reason there is none.</summary>
public sealed record PatchResult(byte[]? Bytes, SyncRecord? Record, PatchFailure Failure);

/// <summary>
/// Changes some groups of a parsed record and writes the canonical form. Properties of the groups
/// the patch does not name, and everything this version does not understand, are written as the
/// file had them. Never mutates its input; does not validate its output (run
/// <c>SyncValidation.checkWritable</c> on <see cref="PatchResult.Record"/> and the byte count).
/// <c>created_at</c> and <c>written_by</c> are never changed.
/// </summary>
public static class RecordPatcher
{
    public static PatchResult Patch(ParsedRecord parsed, RecordPatch patch)
    {
        var r = parsed.Record;
        var content = r.Content;
        var entryType = r.EntryType;
        var contentStamp = r.ContentStamp;
        var project = r.Project;
        var scope = r.Scope;
        var source = r.Source;
        var unknownSource = parsed.UnknownSource;
        var tags = r.Tags;

        var writtenTier = parsed.StoredTier;
        var tierStamp = r.Placement.TierStamp;
        var sticky = r.Placement.Sticky;
        var stickyStamp = r.Placement.StickyStamp;

        if (patch.Content is { } c)
        {
            content = c.Content;
            entryType = c.EntryType;
            contentStamp = c.ContentStamp;
            project = Apply(c.Project, project);
            scope = Apply(c.Scope, scope);
            if (c.Source.Kind != ChangeKind.Keep)
            {
                source = Apply(c.Source, source);
                unknownSource = null;   // the property is named by the patch, so the old unknown text goes
            }
        }

        if (patch.Tags is { } newTags)
            tags = SyncRecordModule.normaliseTags(ListModule.OfSeq(newTags));

        if (patch.Sticky is { } s)
        {
            sticky = s.Sticky;
            stickyStamp = s.StickyStamp;
            var raised = s.Sticky && SyncRecordModule.tierRank(writtenTier) > 0;
            if (raised && patch.Tier is null)
            {
                if (s.TierStampIfRaised is null)
                    return new PatchResult(null, null, PatchFailure.MissingTierStamp);
                writtenTier = Tier.Hot;
                tierStamp = s.TierStampIfRaised;
            }
        }

        if (patch.Tier is { } t)
        {
            // a record that is (now) sticky stays hot: a tier event cannot move a pin
            writtenTier = sticky ? Tier.Hot : t.Tier;
            tierStamp = t.TierStamp;
        }

        var placement = new SyncRecordModule.Placement(sticky ? Tier.Hot : writtenTier, tierStamp, sticky, stickyStamp);
        var next = new SyncRecord(
            r.Id, content, entryType, tags, source!, r.CreatedAtMs, project!, scope!, placement, contentStamp, r.Origin, r.WrittenBy);

        var written = RecordWriter.WriteCore(next, writtenTier, parsed.Unknown, unknownSource);
        return written.NotEncodable
            ? new PatchResult(null, null, PatchFailure.NotEncodable)
            : new PatchResult(written.Bytes, next, PatchFailure.None);
    }

    private static FSharpOption<T> Apply<T>(Change<T> change, FSharpOption<T> current) => change.Kind switch
    {
        ChangeKind.Set => FSharpOption<T>.Some(change.Value!),
        ChangeKind.Clear => FSharpOption<T>.None,
        _ => current,
    };
}
