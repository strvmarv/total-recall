using System.Text.Json;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// Reads one sync record file, which is untrusted input: anyone holding the user's repository
/// credentials can write it. <see cref="Parse"/> never throws; every failure is a
/// <see cref="SyncValidation.Reject"/> with a fixed reason (text taken from the file is sanitised).
/// </summary>
public static class RecordReader
{
    public const int MaxFileBytes = 1_048_576;
    internal const int MaxDepth = 64;

    private static readonly HashSet<string> KnownTop = new(StringComparer.Ordinal)
    {
        "format", "id", "content", "entry_type", "tags", "source", "created_at", "updated_at", "ext",
    };

    private static readonly HashSet<string> KnownTotalRecall = new(StringComparer.Ordinal)
    {
        "project", "scope", "placement", "content_stamp", "origin", "written_by",
    };

    private static readonly HashSet<string> KnownPlacement = new(StringComparer.Ordinal)
    {
        "tier", "tier_stamp", "sticky", "sticky_stamp",
    };

    private static readonly HashSet<string> KnownOrigin = new(StringComparer.Ordinal) { "tool", "hash" };

    public static ParseResult Parse(ReadOnlySpan<byte> utf8, string path)
    {
        try
        {
            // 1. size first: nothing above the cap is parsed at all
            if (utf8.Length > MaxFileBytes)
                return Reject(SyncValidation.Reject.Oversize);

            // 2. no byte-order mark
            if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
                return Reject(SyncValidation.Reject.NewNotJson("byte-order mark"));

            var bytes = utf8.ToArray();

            // 3. strict pre-pass: structure, duplicate names, every string decodable
            var structural = PrePass(bytes);
            if (structural is not null)
                return Reject(structural);

            // 4. extract the raw record and validate it
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
            var (raw, unknown, unknownSourceText) = Extract(doc.RootElement);
            var result = SyncValidation.validate(path, bytes.Length, raw);
            if (result.IsError)
                return Reject(result.ErrorValue);

            var valid = result.ResultValue;
            var unknownSource = valid.Record.Source is null && unknownSourceText is not null ? unknownSourceText : null;
            return new ParseResult(new ParsedRecord(valid.Record, valid.Notes, valid.StoredTier, unknownSource, unknown), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            // Defensive: the pre-pass should have caught everything the reader can throw.
            return Reject(SyncValidation.Reject.NewNotJson("unreadable"));
        }
    }

    private static ParseResult Reject(SyncValidation.Reject reason) => new(null, reason);

    // ---- the pre-pass ----

    /// <summary>
    /// Walks every token once. Returns a reject for: invalid JSON, a root that is not an object,
    /// nesting deeper than 64, content after the root, a duplicate property name in any object
    /// (names compared after decoding), and any string or name that is not valid text (a lone
    /// surrogate escape or invalid UTF-8). Null when the document is sound. A structural error
    /// anywhere in the file wins over a duplicate or bad text found earlier: the first such
    /// finding is remembered while the rest of the file is read.
    /// </summary>
    private static SyncValidation.Reject? PrePass(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth });
        var names = new Stack<HashSet<string>?>();
        var first = true;
        SyncValidation.Reject? found = null;
        try
        {
            while (reader.Read())
            {
                if (first)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                        return SyncValidation.Reject.NewNotJson("the root is not an object");
                    first = false;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        names.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        names.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        names.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                    {
                        var name = reader.GetString()!;
                        if (SyncValidation.hasLoneSurrogate(name))
                            found ??= SyncValidation.Reject.NewNotJson("text that cannot be encoded");
                        else if (!names.Peek()!.Add(name))
                            found ??= SyncValidation.Reject.NewDuplicateKey(SyncValidation.sanitizeText(name));
                        break;
                    }
                    case JsonTokenType.String:
                        if (SyncValidation.hasLoneSurrogate(reader.GetString()!))
                            found ??= SyncValidation.Reject.NewNotJson("text that cannot be encoded");
                        break;
                }
            }

            return first ? SyncValidation.Reject.NewNotJson("empty") : found;
        }
        catch (JsonException)
        {
            return SyncValidation.Reject.NewNotJson("invalid JSON, or nested too deeply");
        }
        catch (InvalidOperationException)
        {
            return SyncValidation.Reject.NewNotJson("text that cannot be encoded");
        }
    }

    // ---- extraction ----

    private enum NodeKind { Absent, Mistyped, Object }

    private readonly record struct Node(NodeKind Kind, JsonElement Element)
    {
        public static readonly Node Missing = new(NodeKind.Absent, default);
        public static readonly Node Wrong = new(NodeKind.Mistyped, default);
    }

    /// <summary>The child object <paramref name="name"/> of <paramref name="parent"/>, or why there is none.</summary>
    private static Node Child(Node parent, string name)
    {
        if (parent.Kind != NodeKind.Object) return parent.Kind == NodeKind.Absent ? Node.Missing : Node.Wrong;
        if (!parent.Element.TryGetProperty(name, out var value)) return Node.Missing;
        return value.ValueKind == JsonValueKind.Object ? new Node(NodeKind.Object, value) : Node.Wrong;
    }

    private static SyncValidation.RawField<string> Str(Node obj, string name)
    {
        if (obj.Kind == NodeKind.Absent) return SyncValidation.RawField<string>.Absent;
        if (obj.Kind == NodeKind.Mistyped) return SyncValidation.RawField<string>.Mistyped;
        if (!obj.Element.TryGetProperty(name, out var v)) return SyncValidation.RawField<string>.Absent;
        return v.ValueKind == JsonValueKind.String
            ? SyncValidation.RawField<string>.NewValue(v.GetString()!)
            : SyncValidation.RawField<string>.Mistyped;
    }

    private static SyncValidation.RawField<bool> Bool(Node obj, string name)
    {
        if (obj.Kind == NodeKind.Absent) return SyncValidation.RawField<bool>.Absent;
        if (obj.Kind == NodeKind.Mistyped) return SyncValidation.RawField<bool>.Mistyped;
        if (!obj.Element.TryGetProperty(name, out var v)) return SyncValidation.RawField<bool>.Absent;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? SyncValidation.RawField<bool>.NewValue(v.GetBoolean())
            : SyncValidation.RawField<bool>.Mistyped;
    }

    private static SyncValidation.RawField<FSharpList<string>> Tags(Node obj, string name)
    {
        if (obj.Kind == NodeKind.Absent) return SyncValidation.RawField<FSharpList<string>>.Absent;
        if (obj.Kind == NodeKind.Mistyped) return SyncValidation.RawField<FSharpList<string>>.Mistyped;
        if (!obj.Element.TryGetProperty(name, out var v)) return SyncValidation.RawField<FSharpList<string>>.Absent;
        if (v.ValueKind != JsonValueKind.Array) return SyncValidation.RawField<FSharpList<string>>.Mistyped;
        var items = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return SyncValidation.RawField<FSharpList<string>>.Mistyped;
            items.Add(e.GetString()!);
        }

        return SyncValidation.RawField<FSharpList<string>>.NewValue(ListModule.OfSeq(items));
    }

    private static SyncValidation.RawField<SyncValidation.RawOrigin> Origin(Node tr)
    {
        var origin = Child(tr, "origin");
        if (origin.Kind == NodeKind.Absent) return SyncValidation.RawField<SyncValidation.RawOrigin>.Absent;
        if (origin.Kind == NodeKind.Mistyped) return SyncValidation.RawField<SyncValidation.RawOrigin>.Mistyped;
        var tool = Str(origin, "tool");
        var hash = Str(origin, "hash");
        return tool.IsValue && hash.IsValue
            ? SyncValidation.RawField<SyncValidation.RawOrigin>.NewValue(
                new SyncValidation.RawOrigin(((SyncValidation.RawField<string>.Value)tool).Item, ((SyncValidation.RawField<string>.Value)hash).Item))
            : SyncValidation.RawField<SyncValidation.RawOrigin>.Mistyped;
    }

    private static List<KeyValuePair<string, JsonElement>> Unknown(Node obj, HashSet<string> known)
    {
        var list = new List<KeyValuePair<string, JsonElement>>();
        if (obj.Kind != NodeKind.Object) return list;
        foreach (var p in obj.Element.EnumerateObject())
            if (!known.Contains(p.Name))
                list.Add(new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()));
        return list;
    }

    private static (SyncValidation.RawSyncRecord Raw, UnknownContent Unknown, string? UnknownSourceText) Extract(JsonElement root)
    {
        var top = new Node(NodeKind.Object, root);
        var ext = Child(top, "ext");
        var tr = Child(ext, "total-recall");
        var placement = Child(tr, "placement");

        var raw = new SyncValidation.RawSyncRecord(
            format: Str(top, "format"),
            id: Str(top, "id"),
            content: Str(top, "content"),
            entryType: Str(top, "entry_type"),
            createdAt: Str(top, "created_at"),
            updatedAt: Str(top, "updated_at"),
            tags: Tags(top, "tags"),
            tier: Str(placement, "tier"),
            sticky: Bool(placement, "sticky"),
            tierStamp: Str(placement, "tier_stamp"),
            stickyStamp: Str(placement, "sticky_stamp"),
            contentStamp: Str(tr, "content_stamp"),
            source: Str(top, "source"),
            project: Str(tr, "project"),
            scope: Str(tr, "scope"),
            writtenBy: Str(tr, "written_by"),
            origin: Origin(tr));

        var extNamespaces = new List<KeyValuePair<string, JsonElement>>();
        if (ext.Kind == NodeKind.Object)
            foreach (var p in ext.Element.EnumerateObject())
                if (p.Name != "total-recall")
                    extNamespaces.Add(new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()));

        var unknown = new UnknownContent(
            Unknown(top, KnownTop),
            extNamespaces,
            Unknown(tr, KnownTotalRecall),
            Unknown(placement, KnownPlacement),
            Unknown(Child(tr, "origin"), KnownOrigin));

        var sourceText = raw.Source.IsValue ? ((SyncValidation.RawField<string>.Value)raw.Source).Item : null;
        return (raw, unknown, sourceText);
    }
}
