using System.Text;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.RecordFixtures;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public class RecordWriterTests
{
    private static string Text(WriteResult r)
    {
        Assert.False(r.NotEncodable);
        return Encoding.UTF8.GetString(r.Bytes!);
    }

    private static void AssertGolden(string expected, SyncRecord record)
    {
        var bytes = RecordWriter.Write(record).Bytes!;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);            // literal bytes, the same on every OS
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "no byte-order mark");
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    // ---- golden bytes ----

    [Fact]
    public void Minimal_record()
    {
        AssertGolden(Minimal, Make());
    }

    [Fact]
    public void Every_optional_field_present()
    {
        var record = Make(
            tags: new[] { "tooling", "a" },
            source: Some(SourceTool.ClaudeCode),
            project: Some("owner/repo"),
            scope: Some("project"),
            origin: Some(new SyncRecordModule.Origin("claude-code", "abc123")),
            writtenBy: Some("laptop"));
        const string Expected = """
            {
              "format": "total-recall/memory/v1",
              "id": "3f9c2a7e-1111",
              "content": "Use bun, not npm, in this repo.",
              "entry_type": "correction",
              "tags": [
                "a",
                "tooling"
              ],
              "source": "claude-code",
              "created_at": "2026-11-12T09:14:03.000Z",
              "updated_at": "2026-11-12T09:14:03.512Z",
              "ext": {
                "total-recall": {
                  "project": "owner/repo",
                  "scope": "project",
                  "placement": {
                    "tier": "warm",
                    "tier_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
                    "sticky": false,
                    "sticky_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e"
                  },
                  "content_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
                  "origin": {
                    "tool": "claude-code",
                    "hash": "abc123"
                  },
                  "written_by": "laptop"
                }
              }
            }

            """;
        AssertGolden(Expected.Replace("\r\n", "\n"), record);
    }

    [Fact]
    public void Quote_and_backslash_are_escaped()
    {
        AssertGolden(Canonical("say \\\"hi\\\" \\\\ end"), Make(content: "say \"hi\" \\ end"));
    }

    [Fact]
    public void Control_characters_use_the_short_escapes_or_lowercase_unicode_escapes()
    {
        var content = "a\n\r\n\t\b\f\0\u0001\u001fz";
        AssertGolden(Canonical("a\\n\\r\\n\\t\\b\\f\\u0000\\u0001\\u001fz"), Make(content: content));
    }

    [Fact]
    public void Every_hex_digit_of_a_control_escape_is_lowercase()
    {
        AssertGolden(Canonical("\\u000b\\u000e\\u0010\\u001a"), Make(content: "\u000b\u000e\u0010\u001a"));
    }

    [Fact]
    public void Everything_else_is_written_literally_as_utf8()
    {
        // DEL, accented, CJK, an emoji (a surrogate pair) and U+2028 are not escaped
        var content = "\u007f caf\u00e9 \u4e2d\u6587 \U0001F600 \u2028";
        var bytes = RecordWriter.Write(Make(content: content)).Bytes!;
        Assert.Equal(Encoding.UTF8.GetBytes(Canonical(content)), bytes);
        Assert.Contains(0x7F, bytes.Select(b => (int)b));
        Assert.Contains("\U0001F600", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Tags_are_written_sorted_and_unique_in_utf16_order()
    {
        var record = Make(tags: new[] { "b", "a", "b", "\uFFFD", "\U0001F600" });
        AssertGolden(Canonical(tagsJson: "[\n    \"a\",\n    \"b\",\n    \"\U0001F600\",\n    \"\uFFFD\"\n  ]"), record);
    }

    // ---- round trips ----

    private static void AssertRoundTrip(string canonical)
    {
        var parsed = RecordReader.Parse(Encoding.UTF8.GetBytes(canonical), RecordPath);
        Assert.NotNull(parsed.Parsed);
        Assert.Equal(canonical, Text(RecordWriter.Write(parsed.Parsed!)));
    }

    [Fact]
    public void Canonical_files_round_trip_byte_for_byte()
    {
        AssertRoundTrip(Minimal);
        AssertRoundTrip(Canonical("say \\\"hi\\\" \\\\ end"));
        AssertRoundTrip(Canonical("a\\n\\r\\n\\t\\b\\f\\u0000\\u0001\\u001fz"));
        AssertRoundTrip(Canonical("caf\u00e9 \u4e2d\u6587 \U0001F600 \u2028"));
        AssertRoundTrip(Canonical(tagsJson: "[\n    \"a\",\n    \"b\"\n  ]"));
    }

    [Fact]
    public void Unknown_content_at_every_level_round_trips_byte_for_byte()
    {
        AssertRoundTrip(Unknowns);
    }

    [Fact]
    public void Unknown_strings_are_re_escaped_by_the_writers_rule_so_an_escaped_letter_comes_back_literal()
    {
        var input = Unknowns.Replace("\"s\": \"q\\\"\\\\\"", "\"s\": \"\\u0041\\/\"");
        var parsed = RecordReader.Parse(Encoding.UTF8.GetBytes(input), RecordPath).Parsed!;
        var expected = Unknowns.Replace("\"s\": \"q\\\"\\\\\"", "\"s\": \"A/\"");
        Assert.Equal(expected, Text(RecordWriter.Write(parsed)));
    }

    [Fact]
    public void A_file_stored_sticky_with_a_colder_tier_is_written_back_as_stored()
    {
        var stored = Minimal.Replace("\"sticky\": false", "\"sticky\": true");   // sticky with tier "warm"
        var parsed = RecordReader.Parse(Encoding.UTF8.GetBytes(stored), RecordPath).Parsed!;
        Assert.Single(parsed.Notes);
        Assert.Equal(Tier.Hot, parsed.Record.Placement.Tier);
        Assert.Equal(stored, Text(RecordWriter.Write(parsed)));
        // Write of the bare model has no stored tier and writes the normalised one
        Assert.Contains("\"tier\": \"hot\"", Text(RecordWriter.Write(parsed.Record)));
    }

    [Fact]
    public void A_hand_edited_updated_at_is_corrected_to_the_content_stamps_time()
    {
        var edited = Minimal.Replace("\"updated_at\": \"2026-11-12T09:14:03.512Z\"", "\"updated_at\": \"2020-01-01T00:00:00.000Z\"");
        var parsed = RecordReader.Parse(Encoding.UTF8.GetBytes(edited), RecordPath).Parsed!;
        Assert.Equal(Minimal, Text(RecordWriter.Write(parsed)));
    }

    [Fact]
    public void A_hand_edited_file_is_written_in_canonical_form()
    {
        var messy = "{ \"format\":\"total-recall/memory/v1\",\r\n \"id\":\"3f9c2a7e-1111\", \"content\":\"Use bun, not npm, in this repo.\"," +
                    "\"entry_type\":\"correction\",\"tags\":[\"b\",\"a\"],\"created_at\":\"2026-11-12T09:14:03.000Z\",\"updated_at\":\"2026-11-12T09:14:03.512Z\"," +
                    "\"ext\":{\"total-recall\":{\"content_stamp\":\"" + StampText + "\",\"placement\":{\"sticky_stamp\":\"" + StampText + "\"," +
                    "\"sticky\":false,\"tier_stamp\":\"" + StampText + "\",\"tier\":\"warm\"}}}}";
        var parsed = RecordReader.Parse(Encoding.UTF8.GetBytes(messy), RecordPath).Parsed!;
        Assert.Equal(Canonical(tagsJson: "[\n    \"a\",\n    \"b\"\n  ]"), Text(RecordWriter.Write(parsed)));
    }

    [Fact]
    public void The_model_written_by_Write_parses_back_to_the_same_record()
    {
        var record = Make(
            tags: new[] { "a", "b" }, source: Some(SourceTool.Hermes), project: Some("p"), scope: Some("global"),
            origin: Some(new SyncRecordModule.Origin("hermes", "h1")), writtenBy: Some("w"), tier: Tier.Hot, sticky: true);
        var parsed = RecordReader.Parse(RecordWriter.Write(record).Bytes!, RecordPath).Parsed!;
        Assert.Equal(record, parsed.Record);
    }

    // ---- what cannot be written ----

    [Fact]
    public void A_lone_surrogate_cannot_be_encoded()
    {
        var lone = "a" + (char)0xD800;
        Assert.True(RecordWriter.Write(Make(content: lone)).NotEncodable);
        Assert.True(RecordWriter.Write(Make(tags: new[] { lone })).NotEncodable);
        Assert.True(RecordWriter.Write(Make(id: "3f9c2a7e" + (char)0xDC00)).NotEncodable);
        Assert.True(RecordWriter.Write(Make(project: Some(lone))).NotEncodable);
        Assert.True(RecordWriter.Write(Make(writtenBy: Some(lone))).NotEncodable);
        Assert.False(RecordWriter.Write(Make(content: "ok \U0001F600")).NotEncodable);
    }

    [Fact]
    public void A_hand_built_record_with_a_null_part_is_not_encodable_and_does_not_throw()
    {
        var good = Make();
        Assert.True(RecordWriter.Write(Make(content: null!)).NotEncodable);
        Assert.True(RecordWriter.Write(Make(id: null!)).NotEncodable);
        Assert.True(RecordWriter.Write(Make(tags: new string[] { null! })).NotEncodable);
        var nullTags = new SyncRecord(
            good.Id, good.Content, good.EntryType, null!, good.Source, good.CreatedAtMs, good.Project, good.Scope,
            good.Placement, good.ContentStamp, good.Origin, good.WrittenBy);
        Assert.True(RecordWriter.Write(nullTags).NotEncodable);
        var noStamp = new SyncRecord(
            good.Id, good.Content, good.EntryType, good.Tags, good.Source, good.CreatedAtMs, good.Project, good.Scope,
            good.Placement, null!, good.Origin, good.WrittenBy);
        Assert.True(RecordWriter.Write(noStamp).NotEncodable);
    }

    [Fact]
    public void A_time_or_stamp_outside_its_domain_cannot_be_written()
    {
        Assert.True(RecordWriter.Write(Make(createdAtMs: SyncStamp.Timestamp.minMs - 1)).NotEncodable);
        Assert.True(RecordWriter.Write(Make(createdAtMs: SyncStamp.Timestamp.maxMs + 1)).NotEncodable);
        var good = Make();
        var bad = new SyncStamp.Stamp(good.ContentStamp.TimeMs, 1_000_000, good.ContentStamp.Machine);
        var withBadCounter = new SyncRecord(
            good.Id, good.Content, good.EntryType, good.Tags, good.Source, good.CreatedAtMs, good.Project, good.Scope,
            good.Placement, bad, good.Origin, good.WrittenBy);
        Assert.True(RecordWriter.Write(withBadCounter).NotEncodable);
    }

    [Fact]
    public void A_very_large_record_is_written_and_read_but_blocked_by_the_writer_limits()
    {
        var record = Make(content: new string('\u0001', 100_000));
        var bytes = RecordWriter.Write(record).Bytes!;
        Assert.True(bytes.Length >= 600_000);
        Assert.True(bytes.Length < RecordReader.MaxFileBytes);
        Assert.NotNull(RecordReader.Parse(bytes, RecordPath).Parsed);
        Assert.True(SyncValidation.checkWritable(record, bytes.Length)!.Value.IsTooLarge);
        Assert.Null(SyncValidation.checkWritable(Make(), RecordWriter.Write(Make()).Bytes!.Length));
    }

    // ---- names ----

    [Fact]
    public void Wire_names_match_the_store_names()
    {
        var sources = new[]
        {
            SourceTool.ClaudeCode, SourceTool.CopilotCli, SourceTool.Opencode, SourceTool.Cursor,
            SourceTool.Cline, SourceTool.Hermes, SourceTool.ManualSource,
        };
        foreach (var s in sources)
            Assert.Equal(SourceToolMapping.ToDbValue(s), SyncRecordModule.sourceToolName(s));

        var types = new[]
        {
            EntryType.Correction, EntryType.Preference, EntryType.Decision, EntryType.Surfaced,
            EntryType.Imported, EntryType.Compacted, EntryType.Ingested,
        };
        foreach (var t in types)
            Assert.Equal(EntryTypeMapping.ToDbValue(t).ToLowerInvariant(), SyncRecordModule.entryTypeName(t));
    }
}
