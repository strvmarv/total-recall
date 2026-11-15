using System.Text.Json;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// A record file that passed validation, plus everything the file holds that this version does
/// not understand, so a write can put it back exactly. Immutable: every patch starts from the
/// same instance.
/// </summary>
public sealed class ParsedRecord
{
    internal ParsedRecord(
        SyncRecord record,
        FSharpList<SyncValidation.Note> notes,
        Tier storedTier,
        string? unknownSource,
        UnknownContent unknown)
    {
        Record = record;
        Notes = notes;
        StoredTier = storedTier;
        UnknownSource = unknownSource;
        Unknown = unknown;
    }

    /// <summary>The validated record; its tier is the normalised one (hot for a sticky record).</summary>
    public SyncRecord Record { get; }

    public FSharpList<SyncValidation.Note> Notes { get; }

    /// <summary>The tier exactly as the file says. Differs from <c>Record.Placement.Tier</c> only for a
    /// <c>sticky: true</c> record that names a colder tier.</summary>
    public Tier StoredTier { get; }

    /// <summary>The text of a <c>source</c> this version does not know (the model holds none for it).</summary>
    public string? UnknownSource { get; }

    internal UnknownContent Unknown { get; }
}

/// <summary>Unknown properties, per object, in the order the file had them. Values are detached clones.
/// Nothing here is ever modified after parsing.</summary>
internal sealed class UnknownContent
{
    public UnknownContent(
        IReadOnlyList<KeyValuePair<string, JsonElement>> top,
        IReadOnlyList<KeyValuePair<string, JsonElement>> extNamespaces,
        IReadOnlyList<KeyValuePair<string, JsonElement>> totalRecall,
        IReadOnlyList<KeyValuePair<string, JsonElement>> placement,
        IReadOnlyList<KeyValuePair<string, JsonElement>> origin)
    {
        Top = top;
        ExtNamespaces = extNamespaces;
        TotalRecall = totalRecall;
        Placement = placement;
        Origin = origin;
    }

    /// <summary>Top-level properties other than the nine known ones.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> Top { get; }

    /// <summary><c>ext</c> namespaces other than <c>total-recall</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> ExtNamespaces { get; }

    /// <summary>Properties of <c>ext["total-recall"]</c> other than the known ones.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> TotalRecall { get; }

    public IReadOnlyList<KeyValuePair<string, JsonElement>> Placement { get; }

    public IReadOnlyList<KeyValuePair<string, JsonElement>> Origin { get; }
}

/// <summary>Exactly one of <see cref="Parsed"/> and <see cref="Rejected"/> is set.</summary>
public sealed class ParseResult
{
    internal ParseResult(ParsedRecord? parsed, SyncValidation.Reject? rejected)
    {
        Parsed = parsed;
        Rejected = rejected;
    }

    public ParsedRecord? Parsed { get; }

    public SyncValidation.Reject? Rejected { get; }

    public bool IsValid => Parsed is not null;
}
