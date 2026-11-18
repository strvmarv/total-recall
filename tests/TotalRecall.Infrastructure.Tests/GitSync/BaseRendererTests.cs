using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public class BaseRendererTests
{
    private const string Path = RecordFixtures.RecordPath;

    private static readonly string Early = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e";
    private static readonly string Late = "2026-11-12T10:00:00.000Z.000000.4f2a9c1e";

    private static ParsedRecord Parse(string text) =>
        RecordReader.Parse(Encoding.UTF8.GetBytes(text), Path).Parsed ?? throw new InvalidOperationException("the fixture is invalid");

    /// <summary>The minimal record with its text, tags, tier, sticky flag and the three stamps changed.</summary>
    private static string Record(
        string content = "Use bun, not npm, in this repo.", string tags = "[]", string tier = "warm", bool sticky = false,
        string contentStamp = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e", string tierStamp = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
        string stickyStamp = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e") =>
        RecordFixtures.Canonical(content, tags)
            .Replace("\"tier\": \"warm\"", $"\"tier\": \"{tier}\"")
            .Replace("\"sticky\": false", $"\"sticky\": {(sticky ? "true" : "false")}")
            .Replace($"\"tier_stamp\": \"{Early}\"", $"\"tier_stamp\": \"{tierStamp}\"")
            .Replace($"\"sticky_stamp\": \"{Early}\"", $"\"sticky_stamp\": \"{stickyStamp}\"")
            .Replace($"\"content_stamp\": \"{Early}\"", $"\"content_stamp\": \"{contentStamp}\"");

    private static SyncMerge.BaseUpdate Update(bool content, bool tags, bool sticky, bool tier)
    {
        static SyncMerge.BaseChange C(bool theirs) => theirs ? SyncMerge.BaseChange.BaseBecomesTheirs : SyncMerge.BaseChange.KeepBase;
        return new SyncMerge.BaseUpdate(C(content), C(tags), C(sticky), C(tier));
    }

    private static SyncRecordModule.SyncRecord Back(string? rendered) =>
        Parse(rendered ?? throw new InvalidOperationException("not rendered")).Record;

    [Fact]
    public void AllTheirs_EqualsTheWriterOfTheRecord_ByteForByte_AndKeepsUnknownContent()
    {
        var theirs = Parse(RecordFixtures.Unknowns);
        var old = Parse(Record("old text"));

        var rendered = BaseRenderer.Render(theirs, old, Update(true, true, true, true));

        Assert.Equal(RecordFixtures.Unknowns, rendered);
        Assert.Contains("\"future\": true", rendered);
        Assert.Contains("\"other-tool\"", rendered);
        Assert.Contains("\"source\": \"windsurf\"", rendered);
    }

    [Fact]
    public void NoOldBase_IsAllTheirs()
    {
        var theirs = Parse(Record("new text"));

        Assert.Equal(Encoding.UTF8.GetString(RecordWriter.Write(theirs).Bytes!), BaseRenderer.Render(theirs, null, Update(false, false, false, false)));
    }

    [Fact]
    public void Mixed_ContentKept_StickyAndTierTaken()
    {
        var old = Parse(Record("old text", tags: "[\"a\"]", tier: "cold", contentStamp: Early, tierStamp: Early));
        var theirs = Parse(Record("new text", tags: "[\"b\"]", tier: "hot", sticky: true, contentStamp: Late, tierStamp: Late, stickyStamp: Late));

        var r = Back(BaseRenderer.Render(theirs, old, Update(false, true, true, true)));

        Assert.Equal("old text", r.Content);
        Assert.Equal(SyncStamp.StampModule.tryParse(Early).Value, r.ContentStamp);
        Assert.Equal(["b"], r.Tags.ToArray()); // tags are always the record's
        Assert.True(r.Placement.Sticky);
        Assert.Equal(Tier.Hot, r.Placement.Tier);
        Assert.Equal(SyncStamp.StampModule.tryParse(Late).Value, r.Placement.StickyStamp);
        Assert.Equal(SyncStamp.StampModule.tryParse(Late).Value, r.Placement.TierStamp);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EveryGroupCombination_TakesEachGroupFromItsSource_AndRoundTrips(bool content, bool sticky, bool tier)
    {
        var old = Parse(Record("old text", tier: "cold", contentStamp: Early, tierStamp: Early, stickyStamp: Early));
        var theirs = Parse(Record("new text", tier: "hot", contentStamp: Late, tierStamp: Late, stickyStamp: Late));

        var r = Back(BaseRenderer.Render(theirs, old, Update(content, true, sticky, tier)));

        Assert.Equal(content ? "new text" : "old text", r.Content);
        Assert.Equal(SyncStamp.StampModule.tryParse(content ? Late : Early).Value, r.ContentStamp);
        Assert.Equal(SyncStamp.StampModule.tryParse(sticky ? Late : Early).Value, r.Placement.StickyStamp);
        Assert.Equal(tier ? Tier.Hot : Tier.Cold, r.Placement.Tier);
        Assert.Equal(SyncStamp.StampModule.tryParse(tier ? Late : Early).Value, r.Placement.TierStamp);
    }

    [Fact]
    public void Mixed_KeepsTheRecordsUnknownContent_AndTheOldSourceWhenContentIsKept()
    {
        var old = Parse(Record("old text"));
        var theirs = Parse(RecordFixtures.Unknowns);

        var rendered = BaseRenderer.Render(theirs, old, Update(false, true, true, true))!;

        Assert.Contains("\"future\": true", rendered);          // unknown content of the record just read survives
        Assert.DoesNotContain("windsurf", rendered);             // the unknown source belongs to the content group, which was kept
        Assert.Contains("old text", rendered);
    }

    [Fact]
    public void ASticyResult_IsWrittenHot_AndAKeptTierUsesTheOldBasesStoredTier()
    {
        var old = Parse(Record(tier: "cold"));
        var theirs = Parse(Record(tier: "hot", sticky: true));

        var sticky = Back(BaseRenderer.Render(theirs, old, Update(true, true, true, false)));
        Assert.True(sticky.Placement.Sticky);
        Assert.Equal(Tier.Hot, sticky.Placement.Tier);

        var plain = Parse(Record(tier: "warm"));
        var kept = BaseRenderer.Render(plain, old, Update(true, true, true, false))!;
        Assert.Contains("\"tier\": \"cold\"", kept);
    }

    [Fact]
    public void ARenderingOverTheFileLimit_IsNull_NotABaseThatCouldNeverBeReadBack()
    {
        // under 1 MiB as the remote file (4 bytes an element), about 3.7 MB once the writer indents it
        var elements = string.Join(",", Enumerable.Repeat("[1]", 250_000));
        var plain = Record("text");
        var text = "{\n  \"x\": [" + elements + "],\n" + plain[(plain.IndexOf('{') + 2)..];
        Assert.True(Encoding.UTF8.GetByteCount(text) < 1_048_576);
        var theirs = Parse(text);

        Assert.Null(BaseRenderer.Render(theirs, null, Update(true, true, true, true)));
        Assert.Null(BaseRenderer.Render(theirs, Parse(Record("old")), Update(true, true, true, true)));
    }

    [Fact]
    public void ARecordThatCannotBeEncoded_RendersNull()
    {
        var theirs = Parse(Record("text"));
        var old = Parse(Record("old"));
        // a record whose content holds a lone surrogate cannot be encoded; build it by hand
        var broken = new SyncRecordModule.SyncRecord(
            theirs.Record.Id, "bad \ud800", theirs.Record.EntryType, theirs.Record.Tags, theirs.Record.Source, theirs.Record.CreatedAtMs,
            theirs.Record.Project, theirs.Record.Scope, theirs.Record.Placement, theirs.Record.ContentStamp, theirs.Record.Origin, theirs.Record.WrittenBy);

        Assert.True(RecordWriter.Write(broken).NotEncodable);
        var brokenParsed = new ParsedRecord(broken, theirs.Notes, theirs.StoredTier, theirs.UnknownSource, theirs.Unknown);
        Assert.Null(BaseRenderer.Render(brokenParsed, old, Update(true, true, true, true)));  // all theirs
        Assert.Null(BaseRenderer.Render(brokenParsed, old, Update(true, true, false, false))); // mixed, content from the broken record
        Assert.NotNull(BaseRenderer.Render(theirs, old, Update(true, true, true, true)));
    }

    [Fact]
    public void AKeptStickyFlag_SurvivesATheirsThatIsNotSticky_AndTheOtherWayRound()
    {
        var pinned = Parse(Record(tier: "hot", sticky: true, stickyStamp: Early));
        var plain = Parse(Record(tier: "warm", sticky: false, stickyStamp: Late));

        var keptPinned = Back(BaseRenderer.Render(plain, pinned, Update(true, true, false, true)));
        Assert.True(keptPinned.Placement.Sticky);
        Assert.Equal(SyncStamp.StampModule.tryParse(Early).Value, keptPinned.Placement.StickyStamp);
        Assert.Equal(Tier.Hot, keptPinned.Placement.Tier); // a sticky result is hot

        var keptPlain = Back(BaseRenderer.Render(pinned, plain, Update(true, true, false, true)));
        Assert.False(keptPlain.Placement.Sticky);
        Assert.Equal(SyncStamp.StampModule.tryParse(Late).Value, keptPlain.Placement.StickyStamp);
    }

    [Fact]
    public void AKeptContentGroup_KeepsTheOldBasesUnknownSource_AndItsOriginExtras()
    {
        var old = Parse(RecordFixtures.Unknowns);
        var theirs = Parse(Record("new text"));

        var rendered = BaseRenderer.Render(theirs, old, Update(false, true, true, true))!;

        Assert.Contains("\"source\": \"windsurf\"", rendered);
        Assert.Contains("\"extra\": 7", rendered); // the origin belongs to the content group
    }

    [Fact]
    public void ATakenContentGroup_TakesTheRecordsOriginExtras_NotTheOldBases()
    {
        var old = Parse(Record("old text"));
        var theirs = Parse(RecordFixtures.Unknowns);

        var rendered = BaseRenderer.Render(theirs, old, Update(true, true, false, false))!;

        Assert.Contains("\"extra\": 7", rendered);
        var kept = BaseRenderer.Render(theirs, old, Update(false, true, true, true))!;
        Assert.DoesNotContain("\"extra\": 7", kept);
    }
}
