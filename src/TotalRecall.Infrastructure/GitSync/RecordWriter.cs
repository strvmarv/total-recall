using System.Text;
using System.Text.Json;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The bytes of a record file, or "cannot be encoded" (a lone surrogate, or a stamp or time outside its domain).</summary>
public sealed record WriteResult(byte[]? Bytes)
{
    public bool NotEncodable => Bytes is null;
}

/// <summary>
/// Writes the one canonical form of a sync record: UTF-8 without a byte-order mark, two-space
/// indent, LF line endings, one trailing newline, properties in a fixed order. The writer is
/// hand-written so the bytes never depend on a runtime's encoder tables: only quote, backslash and
/// the control characters U+0000 to U+001F are escaped, everything else is written as UTF-8.
/// It enforces no size or tag limit (see <c>SyncValidation.checkWritable</c>).
/// </summary>
public static class RecordWriter
{
    /// <summary>Writes a record with no unknown content, using the model's tier.</summary>
    public static WriteResult Write(SyncRecord record) =>
        WriteCore(record, record.Placement.Tier, unknown: null, unknownSource: null);

    /// <summary>
    /// Writes a parsed record back, unknown content included. The stored tier is written (not the
    /// normalised model tier), and <c>updated_at</c> is the time of the content stamp.
    /// </summary>
    public static WriteResult Write(ParsedRecord parsed) =>
        WriteCore(parsed.Record, parsed.StoredTier, parsed.Unknown, parsed.UnknownSource);

    internal static WriteResult WriteCore(SyncRecord r, Tier writtenTier, UnknownContent? unknown, string? unknownSource)
    {
        if (!Encodable(r))
            return new WriteResult(null);

        var root = Build(r, writtenTier, unknown, unknownSource);
        var renderer = new Renderer();
        renderer.Render(root, 0);
        renderer.Newline();
        if (renderer.Bad)
            return new WriteResult(null);

        try
        {
            return new WriteResult(new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(renderer.ToString()));
        }
        catch (EncoderFallbackException)
        {
            return new WriteResult(null);
        }
    }

    /// <summary>False for anything the text form cannot carry, including a hand-built record with a null part.</summary>
    private static bool Encodable(SyncRecord r) =>
        r.Tags is not null
        && r.Placement is not null
        && StampOk(r.ContentStamp)
        && StampOk(r.Placement.TierStamp)
        && StampOk(r.Placement.StickyStamp)
        && SyncStamp.Timestamp.inRange(r.CreatedAtMs);

    private static bool StampOk(SyncStamp.Stamp? s) => s is not null && s.Machine is not null && SyncStamp.StampModule.inDomain(s);

    // ---- the document, as a small tree ----

    private abstract record Node;

    private sealed record Str(string Value) : Node;

    /// <summary>Text copied as it is: <c>true</c>, <c>false</c>, <c>null</c> or a number's original text.</summary>
    private sealed record Raw(string Text) : Node;

    private sealed record Obj(List<KeyValuePair<string, Node>> Props) : Node
    {
        public Obj() : this(new List<KeyValuePair<string, Node>>()) { }

        public Obj Add(string name, Node value)
        {
            Props.Add(new KeyValuePair<string, Node>(name, value));
            return this;
        }

        public Obj AddUnknown(IReadOnlyList<KeyValuePair<string, JsonElement>>? unknown)
        {
            if (unknown is not null)
                foreach (var p in unknown)
                    Add(p.Key, FromElement(p.Value));
            return this;
        }
    }

    private sealed record Arr(List<Node> Items) : Node;

    private static Node FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new Obj();
                foreach (var p in e.EnumerateObject())
                    o.Add(p.Name, FromElement(p.Value));
                return o;
            case JsonValueKind.Array:
                return new Arr(e.EnumerateArray().Select(FromElement).ToList());
            case JsonValueKind.String:
                return new Str(e.GetString()!);
            case JsonValueKind.Number:
                return new Raw(e.GetRawText());
            case JsonValueKind.True:
                return new Raw("true");
            case JsonValueKind.False:
                return new Raw("false");
            default:
                return new Raw("null");
        }
    }

    private static Node Build(SyncRecord r, Tier writtenTier, UnknownContent? unknown, string? unknownSource)
    {
        var placement = new Obj()
            .Add("tier", new Str(SyncRecordModule.tierName(writtenTier)))
            .Add("tier_stamp", new Str(SyncStamp.StampModule.format(r.Placement.TierStamp)))
            .Add("sticky", new Raw(r.Placement.Sticky ? "true" : "false"))
            .Add("sticky_stamp", new Str(SyncStamp.StampModule.format(r.Placement.StickyStamp)))
            .AddUnknown(unknown?.Placement);

        var tr = new Obj();
        if (r.Project is not null) tr.Add("project", new Str(r.Project.Value));
        if (r.Scope is not null) tr.Add("scope", new Str(r.Scope.Value));
        tr.Add("placement", placement);
        tr.Add("content_stamp", new Str(SyncStamp.StampModule.format(r.ContentStamp)));
        if (r.Origin is not null)
            tr.Add("origin", new Obj().Add("tool", new Str(r.Origin.Value.Tool)).Add("hash", new Str(r.Origin.Value.Hash)).AddUnknown(unknown?.Origin));
        if (r.WrittenBy is not null) tr.Add("written_by", new Str(r.WrittenBy.Value));
        tr.AddUnknown(unknown?.TotalRecall);

        var ext = new Obj().Add("total-recall", tr).AddUnknown(unknown?.ExtNamespaces);

        var root = new Obj()
            .Add("format", new Str("total-recall/memory/v1"))
            .Add("id", new Str(r.Id))
            .Add("content", new Str(r.Content))
            .Add("entry_type", new Str(SyncRecordModule.entryTypeName(r.EntryType)))
            .Add("tags", new Arr(SyncRecordModule.normaliseTags(r.Tags).Select(t => (Node)new Str(t)).ToList()));
        if (r.Source is not null) root.Add("source", new Str(SyncRecordModule.sourceToolName(r.Source.Value)));
        else if (unknownSource is not null) root.Add("source", new Str(unknownSource));
        root.Add("created_at", new Str(SyncStamp.Timestamp.format(r.CreatedAtMs)))
            .Add("updated_at", new Str(SyncStamp.Timestamp.format(r.ContentStamp.TimeMs)))
            .Add("ext", ext)
            .AddUnknown(unknown?.Top);
        return root;
    }

    // ---- the layout ----

    private sealed class Renderer
    {
        private readonly StringBuilder _sb = new();

        public bool Bad { get; private set; }

        public void Newline() => _sb.Append('\n');

        public override string ToString() => _sb.ToString();

        public void Render(Node node, int indent)
        {
            switch (node)
            {
                case Str s:
                    WriteString(s.Value);
                    break;
                case Raw raw:
                    _sb.Append(raw.Text);
                    break;
                case Obj { Props.Count: 0 }:
                    _sb.Append("{}");
                    break;
                case Obj o:
                    _sb.Append("{\n");
                    for (var i = 0; i < o.Props.Count; i++)
                    {
                        _sb.Append(' ', indent + 2);
                        WriteString(o.Props[i].Key);
                        _sb.Append(": ");
                        Render(o.Props[i].Value, indent + 2);
                        _sb.Append(i < o.Props.Count - 1 ? ",\n" : "\n");
                    }

                    _sb.Append(' ', indent).Append('}');
                    break;
                case Arr { Items.Count: 0 }:
                    _sb.Append("[]");
                    break;
                case Arr a:
                    _sb.Append("[\n");
                    for (var i = 0; i < a.Items.Count; i++)
                    {
                        _sb.Append(' ', indent + 2);
                        Render(a.Items[i], indent + 2);
                        _sb.Append(i < a.Items.Count - 1 ? ",\n" : "\n");
                    }

                    _sb.Append(' ', indent).Append(']');
                    break;
            }
        }

        private void WriteString(string? text)
        {
            if (text is null)
            {
                Bad = true;
                return;
            }

            _sb.Append('"');
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            _sb.Append("\\u00").Append(HexDigit(c >> 4)).Append(HexDigit(c & 0xF));
                        }
                        else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            _sb.Append(c).Append(text[i + 1]);
                            i++;
                        }
                        else if (char.IsSurrogate(c))
                        {
                            Bad = true;
                        }
                        else
                        {
                            _sb.Append(c);
                        }

                        break;
                }
            }

            _sb.Append('"');
        }

        private static char HexDigit(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));
    }
}
