using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public class RecordReaderTests
{
    private const string Path = "memories/3f/3f9c2a7e-1111.json";
    private const string Stamp = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e";

    // A hand-written valid record. Tests change it with Edit so each case differs in one place.
    private static readonly string Good = """
        {
          "format": "total-recall/memory/v1",
          "id": "3f9c2a7e-1111",
          "content": "Use bun, not npm, in this repo.",
          "entry_type": "correction",
          "tags": ["tooling"],
          "source": "claude-code",
          "created_at": "2026-11-12T09:14:03.000Z",
          "updated_at": "2026-11-12T09:14:03.512Z",
          "ext": {
            "total-recall": {
              "project": "owner/repo",
              "placement": {
                "tier": "warm",
                "tier_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
                "sticky": false,
                "sticky_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e"
              },
              "content_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
              "written_by": "laptop"
            }
          }
        }
        """.Replace("\r\n", "\n");

    private static string Edit(string from, string to)
    {
        Assert.Contains(from, Good);
        return Good.Replace(from, to);
    }

    private static ParseResult Parse(string json, string path = Path) => RecordReader.Parse(Encoding.UTF8.GetBytes(json), path);

    private static ParseResult ParseBytes(byte[] bytes, string path = Path) => RecordReader.Parse(bytes, path);

    private static void AssertRejected(ParseResult r)
    {
        Assert.Null(r.Parsed);
        Assert.NotNull(r.Rejected);
    }

    // ---- accepted ----

    [Fact]
    public void A_valid_record_parses()
    {
        var r = Parse(Good);
        Assert.NotNull(r.Parsed);
        var p = r.Parsed!;
        Assert.Equal("3f9c2a7e-1111", p.Record.Id);
        Assert.Equal("Use bun, not npm, in this repo.", p.Record.Content);
        Assert.Equal(Tier.Warm, p.StoredTier);
        Assert.Equal(Tier.Warm, p.Record.Placement.Tier);
        Assert.True(p.Notes.IsEmpty);
        Assert.Null(p.UnknownSource);
        Assert.Equal("owner/repo", p.Record.Project!.Value);
        Assert.Equal("laptop", p.Record.WrittenBy!.Value);
    }

    [Fact]
    public void Crlf_line_endings_and_extra_whitespace_parse()
    {
        Assert.NotNull(Parse(Good.Replace("\n", "\r\n")).Parsed);
        Assert.NotNull(Parse("  \t" + Good.Replace(",\n", " ,\n\n\n  ") + "  \n\n").Parsed);
    }

    [Fact]
    public void Emoji_accented_and_cjk_content_parses()
    {
        var r = Parse(Edit("Use bun, not npm, in this repo.", "caf\u00e9 \u4e2d\u6587 \U0001F600"));
        Assert.Equal("caf\u00e9 \u4e2d\u6587 \U0001F600", r.Parsed!.Record.Content);
    }

    [Fact]
    public void A_file_of_exactly_one_mebibyte_is_accepted_and_one_byte_more_is_oversize()
    {
        var exact = new byte[RecordReader.MaxFileBytes];
        var body = Encoding.UTF8.GetBytes(Good);
        Array.Fill(exact, (byte)' ');
        body.CopyTo(exact, 0);
        Assert.NotNull(ParseBytes(exact).Parsed);

        var over = new byte[RecordReader.MaxFileBytes + 1];
        Array.Fill(over, (byte)' ');
        body.CopyTo(over, 0);
        Assert.True(ParseBytes(over).Rejected!.IsOversize);
    }

    [Fact]
    public void An_oversize_file_is_rejected_before_it_is_parsed()
    {
        var notJson = new byte[RecordReader.MaxFileBytes + 1];
        Array.Fill(notJson, (byte)'x');
        Assert.True(ParseBytes(notJson).Rejected!.IsOversize);

        var deep = new byte[RecordReader.MaxFileBytes + 1];
        Array.Fill(deep, (byte)'[');
        Assert.True(ParseBytes(deep).Rejected!.IsOversize);
    }

    [Fact]
    public void Two_hundred_fifty_six_tags_of_two_hundred_fifty_six_units_are_accepted()
    {
        var tags = string.Join(",", Enumerable.Range(0, 256).Select(i => "\"" + i.ToString("D3") + new string('a', 253) + "\""));
        Assert.NotNull(Parse(Edit("[\"tooling\"]", "[" + tags + "]")).Parsed);
        Assert.True(Parse(Edit("[\"tooling\"]", "[" + tags + ",\"extra\"]")).Rejected!.IsBadTags);
    }

    // ---- rejected ----

    public static TheoryData<string, string> Structural => new()
    {
        { "invalid JSON", "{ \"format\": " },
        { "an array root", "[]" },
        { "a string root", "\"x\"" },
        { "an empty file", "" },
        { "trailing text", "{}x" },
        { "a second root value", "{} {}" },
        { "a comment", "{ /* c */ }" },
    };

    [Theory]
    [MemberData(nameof(Structural))]
    public void Structural_problems_are_not_json(string name, string json)
    {
        var r = Parse(json);
        AssertRejected(r);
        Assert.True(r.Rejected!.IsNotJson, name);
    }

    [Fact]
    public void A_byte_order_mark_is_rejected()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Good)).ToArray();
        Assert.True(ParseBytes(bytes).Rejected!.IsNotJson);
    }

    [Fact]
    public void Nesting_of_exactly_64_levels_is_accepted_and_65_is_rejected()
    {
        // the root object is level 1; the unknown property holds arrays nested inside it
        string Nested(int arrays) => Edit("\"tags\":", "\"x\": " + new string('[', arrays) + new string(']', arrays) + ", \"tags\":");
        Assert.NotNull(Parse(Nested(63)).Parsed);
        Assert.True(Parse(Nested(64)).Rejected!.IsNotJson);
    }

    [Fact]
    public void A_deeply_nested_file_is_rejected_without_overflowing_the_stack()
    {
        var deep = new string('[', 5000) + new string(']', 5000);
        Assert.True(Parse(deep).Rejected!.IsNotJson);
        Assert.True(Parse(Edit("\"tags\":", "\"x\": " + deep + ", \"tags\":")).Rejected!.IsNotJson);
    }

    [Fact]
    public void Duplicate_names_are_rejected_at_every_depth()
    {
        Assert.True(Parse(Edit("\"id\": \"3f9c2a7e-1111\",", "\"id\": \"3f9c2a7e-1111\", \"id\": \"3f9c2a7e-1111\",")).Rejected!.IsDuplicateKey);
        Assert.True(Parse(Edit("\"written_by\": \"laptop\"", "\"written_by\": \"laptop\", \"written_by\": \"x\"")).Rejected!.IsDuplicateKey);
        Assert.True(Parse(Edit("\"sticky\": false,", "\"sticky\": false, \"sticky\": true,")).Rejected!.IsDuplicateKey);
        Assert.True(Parse(Edit("\"tags\":", "\"x\": {\"a\":1,\"a\":2}, \"tags\":")).Rejected!.IsDuplicateKey);
        Assert.True(Parse(Edit("\"tags\":", "\"x\": [{\"a\":1},{\"b\":1,\"b\":2}], \"tags\":")).Rejected!.IsDuplicateKey);
    }

    [Fact]
    public void Names_are_compared_after_unescaping()
    {
        // "cont\u0065nt" is "content"
        Assert.True(Parse(Edit("\"content\":", "\"cont\\u0065nt\": \"other\", \"content\":")).Rejected!.IsDuplicateKey);
    }

    [Fact]
    public void The_same_name_in_two_different_objects_is_fine()
    {
        Assert.NotNull(Parse(Edit("\"tags\":", "\"x\": {\"a\":1}, \"y\": {\"a\":2}, \"tags\":")).Parsed);
    }

    [Fact]
    public void A_duplicate_key_reports_before_an_unknown_format()
    {
        var json = Edit("\"format\": \"total-recall/memory/v1\",", "\"format\": \"total-recall/memory/v2\", \"id\": \"x\", \"id\": \"y\",");
        Assert.True(Parse(json).Rejected!.IsDuplicateKey);
        Assert.True(Parse(Edit("total-recall/memory/v1", "total-recall/memory/v2")).Rejected!.IsUnknownFormat);
    }

    [Fact]
    public void Text_that_cannot_be_encoded_is_rejected_wherever_it_is()
    {
        Assert.True(Parse(Edit("3f9c2a7e-1111\",", "3f9c2a7e-1111\\ud800\",")).Rejected!.IsNotJson);   // id
        Assert.True(Parse(Edit("Use bun", "Use \\ud800 bun")).Rejected!.IsNotJson);                    // content
        Assert.True(Parse(Edit("[\"tooling\"]", "[\"to\\udc00ling\"]")).Rejected!.IsNotJson);          // a tag
        Assert.True(Parse(Edit("\"tags\":", "\"x\": \"\\ud800\", \"tags\":")).Rejected!.IsNotJson);    // an unknown value
        Assert.True(Parse(Edit("\"tags\":", "\"\\ud800\": 1, \"tags\":")).Rejected!.IsNotJson);        // an unknown property name
        Assert.NotNull(Parse(Edit("Use bun", "Use \\ud83d\\ude00 bun")).Parsed);                       // a valid pair is fine
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Good);
        var at = Array.IndexOf(bytes, (byte)'U');                    // the "U" of "Use bun"
        var bad = bytes.Take(at).Concat(new byte[] { 0xC3, 0x28 }).Concat(bytes.Skip(at + 1)).ToArray();
        Assert.True(ParseBytes(bad).Rejected!.IsNotJson);
        var inName = Encoding.UTF8.GetBytes(Edit("\"tags\":", "\"x\": 1, \"tags\":"));
        var xAt = Array.IndexOf(inName, (byte)'x');
        var badName = inName.Take(xAt).Concat(new byte[] { 0xFF }).Concat(inName.Skip(xAt + 1)).ToArray();
        Assert.True(ParseBytes(badName).Rejected!.IsNotJson);
    }

    [Fact]
    public void A_value_of_the_wrong_json_type_is_rejected()
    {
        Assert.True(Parse(Edit("[\"tooling\"]", "\"tooling\"")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("[\"tooling\"]", "[\"a\", 1]")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"Use bun, not npm, in this repo.\"", "42")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"sticky\": false", "\"sticky\": \"no\"")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"project\": \"owner/repo\"", "\"project\": null")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"placement\": {", "\"placement\": 1, \"x\": {")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"ext\": {", "\"ext\": 1, \"x\": {")).Rejected!.IsWrongType);
    }

    [Fact]
    public void An_origin_without_both_strings_is_a_wrong_type()
    {
        Assert.True(Parse(Edit("\"written_by\": \"laptop\"", "\"origin\": {\"tool\": \"claude-code\"}")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"written_by\": \"laptop\"", "\"origin\": {\"tool\": \"claude-code\", \"hash\": 5}")).Rejected!.IsWrongType);
        Assert.True(Parse(Edit("\"written_by\": \"laptop\"", "\"origin\": []")).Rejected!.IsWrongType);
        var ok = Parse(Edit("\"written_by\": \"laptop\"", "\"origin\": {\"tool\": \"claude-code\", \"hash\": \"abc\"}"));
        Assert.Equal("abc", ok.Parsed!.Record.Origin!.Value.Hash);
    }

    [Fact]
    public void A_missing_stamp_and_an_id_that_does_not_match_the_path_are_rejected()
    {
        Assert.True(Parse(Edit("\"content_stamp\": \"" + Stamp + "\",", "")).Rejected!.IsMissingField);
        Assert.True(Parse(Good, "memories/aa/3f9c2a7e-1111.json").Rejected!.IsIdPathMismatch);
        Assert.True(Parse(Good, "memories/3f/3f9c2a7e-2222.json").Rejected!.IsIdPathMismatch);
        Assert.True(Parse(Good, "elsewhere.json").Rejected!.IsIdPathMismatch);
    }

    [Fact]
    public void Reject_text_from_the_file_is_sanitised()
    {
        var name = "a\\n\\u0007" + new string('b', 100);
        var r = Parse(Edit("\"tags\":", "\"" + name + "\": 1, \"" + name + "\": 2, \"tags\":")).Rejected!;
        Assert.True(r.IsDuplicateKey);
        var text = ((SyncValidation.Reject.DuplicateKey)r).Item;
        Assert.True(text.Length <= 64);
        Assert.StartsWith("a??", text);
        Assert.DoesNotContain(text, char.IsControl);

        var pairAtCut = new string('c', 63) + "\\ud83d\\ude00tail";
        var r2 = Parse(Edit("\"tags\":", "\"" + pairAtCut + "\": 1, \"" + pairAtCut + "\": 2, \"tags\":")).Rejected!;
        Assert.Equal(new string('c', 63), ((SyncValidation.Reject.DuplicateKey)r2).Item);
    }

    [Fact]
    public void Not_json_carries_a_fixed_reason_never_an_exception_message()
    {
        var allowed = new[]
        {
            "byte-order mark", "the root is not an object", "invalid JSON, or nested too deeply",
            "text that cannot be encoded", "empty", "unreadable",
        };
        foreach (var json in new[] { "{", "[]", "", "{}x", Edit("Use bun", "Use \\ud800") })
        {
            var r = Parse(json).Rejected!;
            Assert.True(r.IsNotJson);
            Assert.Contains(((SyncValidation.Reject.NotJson)r).Item, allowed);
        }
    }

    // ---- unknown content ----

    [Fact]
    public void Unknown_content_is_kept_per_object_in_file_order()
    {
        var json = Good
            .Replace("\"ext\": {", "\"zeta\": {\"n\": 1.50}, \"ext\": {\"other-tool\": {\"k\": [1, 2]}, ")
            .Replace("\"project\": \"owner/repo\",", "\"project\": \"owner/repo\", \"future\": true, \"alpha\": null,")
            .Replace("\"tier\": \"warm\",", "\"tier\": \"warm\", \"note\": \"p\",")
            .Replace("\"written_by\": \"laptop\"", "\"written_by\": \"laptop\", \"origin\": {\"tool\": \"claude-code\", \"hash\": \"h\", \"extra\": 7}")
            .Replace("\"source\": \"claude-code\"", "\"source\": \"windsurf\"");
        var p = Parse(json).Parsed!;
        Assert.Equal(new[] { "zeta" }, p.Unknown.Top.Select(x => x.Key));
        Assert.Equal(new[] { "other-tool" }, p.Unknown.ExtNamespaces.Select(x => x.Key));
        Assert.Equal(new[] { "future", "alpha" }, p.Unknown.TotalRecall.Select(x => x.Key));
        Assert.Equal(new[] { "note" }, p.Unknown.Placement.Select(x => x.Key));
        Assert.Equal(new[] { "extra" }, p.Unknown.Origin.Select(x => x.Key));
        Assert.Equal("windsurf", p.UnknownSource);
        Assert.Null(p.Record.Source);
        Assert.Equal("1.50", p.Unknown.Top[0].Value.GetProperty("n").GetRawText());
    }

    [Fact]
    public void A_known_source_leaves_no_unknown_source()
    {
        Assert.Null(Parse(Good).Parsed!.UnknownSource);
    }

    [Fact]
    public void A_sticky_record_with_a_colder_tier_is_normalised_but_remembers_the_stored_tier()
    {
        var p = Parse(Edit("\"sticky\": false", "\"sticky\": true")).Parsed!;
        Assert.Equal(Tier.Hot, p.Record.Placement.Tier);
        Assert.Equal(Tier.Warm, p.StoredTier);
        Assert.Single(p.Notes);
    }

    // ---- never throws ----

    [Fact]
    public void Parse_never_throws_and_always_returns_exactly_one_outcome()
    {
        var rng = new Random(20261115);
        var good = Encoding.UTF8.GetBytes(Good);
        var fragments = new[]
        {
            "{", "}", "[", "]", "\"", "\\", "\\u", "\\ud800", "\\udc00", ":", ",", "\"a\"", "1", "-", "1e999", "true", "null",
            "\"format\"", "\"ext\"", "\"tags\"", "\u00e9", "\U0001F600", "\0", "\n", " ", "/*", "//",
        };

        for (var i = 0; i < 3000; i++)
        {
            byte[] input = (i % 4) switch
            {
                0 => RandomBytes(rng, rng.Next(0, 300)),
                1 => good.Take(rng.Next(0, good.Length)).ToArray(),
                2 => Mutate(rng, good),
                _ => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, rng.Next(1, 40)).Select(_ => fragments[rng.Next(fragments.Length)]))),
            };

            var r = RecordReader.Parse(input, rng.Next(3) == 0 ? "memories/3f/3f9c2a7e-1111.json" : Path);
            Assert.True((r.Parsed is null) != (r.Rejected is null), $"iteration {i}");
            if (r.Rejected is not null)
                Assert.NotEmpty(SyncValidation.RejectModule.describe(r.Rejected));
        }
    }

    private static byte[] RandomBytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    private static byte[] Mutate(Random rng, byte[] source)
    {
        var copy = (byte[])source.Clone();
        for (var k = 0; k < rng.Next(1, 4); k++)
            copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
        return copy;
    }
}
