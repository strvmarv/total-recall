using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.RecordFixtures;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public class RecordPatcherTests
{
    private const string Later = "2026-11-12T10:00:00.000Z.000000.0000000a";
    private const string Later2 = "2026-11-12T11:00:00.000Z.000001.0000000b";

    // The sample: warm, not sticky, an unknown source, unknown content at every level.
    private static string Warm => Unknowns;
    private static string Hot => Unknowns.Replace("\"tier\": \"warm\"", "\"tier\": \"hot\"");
    private static string HotSticky => Hot.Replace("\"sticky\": false", "\"sticky\": true");
    private static string StoredWarmSticky => Warm.Replace("\"sticky\": false", "\"sticky\": true");

    private static ParsedRecord Load(string text)
    {
        var r = RecordReader.Parse(Encoding.UTF8.GetBytes(text), RecordPath);
        Assert.NotNull(r.Parsed);
        return r.Parsed!;
    }

    private static (string Text, PatchResult Result) Apply(string baseText, RecordPatch patch)
    {
        var result = RecordPatcher.Patch(Load(baseText), patch);
        Assert.Equal(PatchFailure.None, result.Failure);
        return (Encoding.UTF8.GetString(result.Bytes!), result);
    }

    /// <summary>The property names of the lines that differ between two texts (lines compared as a multiset).</summary>
    private static SortedSet<string> ChangedKeys(string before, string after)
    {
        var old = before.Split('\n').ToList();
        var changed = new List<string>();
        foreach (var line in after.Split('\n'))
            if (!old.Remove(line))
                changed.Add(line);
        changed.AddRange(old);
        return new SortedSet<string>(changed.Select(l =>
        {
            var t = l.Trim();
            var colon = t.IndexOf("\":", StringComparison.Ordinal);
            return colon > 0 ? t[1..colon] : t.TrimEnd(',');
        }));
    }

    private static SortedSet<string> Keys(params string[] keys) => new(keys);

    private static string Prop(string text, string name)
    {
        var line = text.Split('\n').First(l => l.TrimStart().StartsWith("\"" + name + "\":", StringComparison.Ordinal));
        return line.Trim().TrimEnd(',');
    }

    // ---- no patch, one group at a time ----

    [Fact]
    public void An_empty_patch_returns_the_original_bytes()
    {
        var (text, _) = Apply(Warm, new RecordPatch());
        Assert.Equal(Warm, text);
    }

    [Fact]
    public void A_content_patch_changes_only_the_content_group()
    {
        var patch = new RecordPatch(Content: new ContentPatch("New text.", EntryType.Decision, Stamp(Later), Scope: Change<string>.Set("global")));
        var (text, result) = Apply(Warm, patch);
        Assert.Equal(Keys("content", "entry_type", "scope", "content_stamp", "updated_at"), ChangedKeys(Warm, text));
        Assert.Equal("\"updated_at\": \"2026-11-12T10:00:00.000Z\"", Prop(text, "updated_at"));
        Assert.Equal("\"windsurf\"", Prop(text, "source")[10..]);                  // an unknown source is kept
        Assert.Equal("New text.", result.Record!.Content);
        Assert.Equal(EntryType.Decision, result.Record.EntryType);
    }

    [Fact]
    public void Source_and_project_changes_are_individually_optional()
    {
        ContentPatch Make(Change<string> project = default, Change<SourceTool> source = default) =>
            new("Use bun, not npm, in this repo.", EntryType.Correction, Stamp(Later), Project: project, Source: source);

        var keep = Apply(Warm, new RecordPatch(Content: Make())).Text;
        Assert.Contains("\"source\": \"windsurf\"", keep);              // no Source named: the unknown text stays
        Assert.Contains("\"project\": \"owner/repo\"", keep);

        var set = Apply(Warm, new RecordPatch(Content: Make(source: Change<SourceTool>.Set(SourceTool.Cursor)))).Text;
        Assert.Contains("\"source\": \"cursor\"", set);
        Assert.DoesNotContain("windsurf", set);

        var clear = Apply(Warm, new RecordPatch(Content: Make(source: Change<SourceTool>.Clear, project: Change<string>.Clear))).Text;
        Assert.DoesNotContain("\"source\"", clear);
        Assert.DoesNotContain("\"project\"", clear);
    }

    [Fact]
    public void A_tags_patch_writes_the_tags_sorted_and_unique_and_touches_nothing_else()
    {
        var (text, result) = Apply(Warm, new RecordPatch(Tags: new[] { "b", "a", "b" }));
        Assert.Equal(Warm.Replace("\"tags\": [],", "\"tags\": [\n    \"a\",\n    \"b\"\n  ],"), text);
        Assert.Equal(new[] { "a", "b" }, result.Record!.Tags);
        Assert.Contains("\"source\": \"windsurf\"", text);             // an unknown source survives a tags-only patch
    }

    // ---- the tier / sticky table ----

    [Fact]
    public void Sticky_only_and_not_raised_changes_only_the_sticky_group()
    {
        var (pin, _) = Apply(Hot, new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later))));
        Assert.Equal(Keys("sticky", "sticky_stamp"), ChangedKeys(Hot, pin));
        var (unpin, _) = Apply(HotSticky, new RecordPatch(Sticky: new StickyPatch(false, Stamp(Later))));
        Assert.Equal(Keys("sticky", "sticky_stamp"), ChangedKeys(HotSticky, unpin));
    }

    [Fact]
    public void Sticky_only_and_raised_also_writes_hot_with_the_tier_stamp_for_the_raise()
    {
        var patch = new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later), Stamp(Later2)));
        var (text, result) = Apply(Warm, patch);
        Assert.Equal(Keys("tier", "tier_stamp", "sticky", "sticky_stamp"), ChangedKeys(Warm, text));
        Assert.Equal("\"tier\": \"hot\"", Prop(text, "tier"));
        Assert.Contains(Later2, Prop(text, "tier_stamp"));
        Assert.Contains(Later, Prop(text, "sticky_stamp"));
        Assert.Equal(Tier.Hot, result.Record!.Placement.Tier);
    }

    [Fact]
    public void A_raising_pin_without_a_tier_stamp_is_refused()
    {
        var result = RecordPatcher.Patch(Load(Warm), new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later))));
        Assert.Equal(PatchFailure.MissingTierStamp, result.Failure);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void Tier_only_on_a_record_that_is_not_sticky_writes_the_patchs_tier_and_stamp()
    {
        var (text, _) = Apply(Warm, new RecordPatch(Tier: new TierPatch(Tier.Cold, Stamp(Later))));
        Assert.Equal(Keys("tier", "tier_stamp"), ChangedKeys(Warm, text));
        Assert.Equal("\"tier\": \"cold\"", Prop(text, "tier"));
    }

    [Fact]
    public void Tier_only_on_a_sticky_record_keeps_it_hot_and_takes_the_stamp()
    {
        var (text, result) = Apply(HotSticky, new RecordPatch(Tier: new TierPatch(Tier.Cold, Stamp(Later))));
        Assert.Equal(Keys("tier_stamp"), ChangedKeys(HotSticky, text));
        Assert.Equal("\"tier\": \"hot\"", Prop(text, "tier"));
        Assert.Equal(Tier.Hot, result.Record!.Placement.Tier);
    }

    [Fact]
    public void Sticky_and_tier_together_when_the_result_is_sticky_is_hot_with_the_tier_patchs_stamp()
    {
        var patch = new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later), Stamp(Later)), Tier: new TierPatch(Tier.Cold, Stamp(Later2)));
        var (text, _) = Apply(Warm, patch);
        Assert.Equal("\"tier\": \"hot\"", Prop(text, "tier"));
        Assert.Contains(Later2, Prop(text, "tier_stamp"));                     // the patch's, not TierStampIfRaised
        Assert.Equal("\"sticky\": true", Prop(text, "sticky"));
        Assert.Contains(Later, Prop(text, "sticky_stamp"));
    }

    [Fact]
    public void Sticky_and_tier_together_when_the_result_is_not_sticky_writes_both_as_patched()
    {
        var patch = new RecordPatch(Sticky: new StickyPatch(false, Stamp(Later)), Tier: new TierPatch(Tier.Cold, Stamp(Later2)));
        var (text, result) = Apply(HotSticky, patch);
        Assert.Equal("\"tier\": \"cold\"", Prop(text, "tier"));
        Assert.Contains(Later2, Prop(text, "tier_stamp"));
        Assert.Equal("\"sticky\": false", Prop(text, "sticky"));
        Assert.Equal(Tier.Cold, result.Record!.Placement.Tier);
    }

    [Fact]
    public void A_tier_stamp_for_the_raise_is_ignored_when_a_tier_part_is_present()
    {
        var withBoth = new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later), Stamp(Later)), Tier: new TierPatch(Tier.Warm, Stamp(Later2)));
        var withoutRaiseStamp = new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later)), Tier: new TierPatch(Tier.Warm, Stamp(Later2)));
        Assert.Equal(Apply(Warm, withBoth).Text, Apply(Warm, withoutRaiseStamp).Text);
    }

    [Fact]
    public void A_content_patch_on_a_record_stored_sticky_with_a_warm_tier_leaves_the_stored_tier_and_stamp()
    {
        var patch = new RecordPatch(Content: new ContentPatch("Changed.", EntryType.Correction, Stamp(Later)));
        var (text, result) = Apply(StoredWarmSticky, patch);
        Assert.Equal(Keys("content", "content_stamp", "updated_at"), ChangedKeys(StoredWarmSticky, text));
        Assert.Equal("\"tier\": \"warm\"", Prop(text, "tier"));
        Assert.Equal("\"sticky\": true", Prop(text, "sticky"));
        Assert.Equal(Tier.Hot, result.Record!.Placement.Tier);                 // the model is normalised
    }

    // ---- invariants of every patch ----

    [Fact]
    public void Unknown_content_survives_every_part()
    {
        var patches = new[]
        {
            new RecordPatch(Content: new ContentPatch("x", EntryType.Decision, Stamp(Later))),
            new RecordPatch(Tags: new[] { "t" }),
            new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later), Stamp(Later2))),
            new RecordPatch(Tier: new TierPatch(Tier.Cold, Stamp(Later))),
        };
        foreach (var patch in patches)
        {
            var text = Apply(Warm, patch).Text;
            foreach (var kept in new[] { "\"note\": \"p\"", "\"extra\": 7", "\"future\": true", "\"alpha\": null", "\"n2\": 1.50", "\"n3\": 1E400", "\"another-tool\": {}", "\"yota\": []", "\"alpha-top\": \"z\"", "\"zeta\"" })
                Assert.Contains(kept, text);
            Assert.Equal(Prop(Warm, "created_at"), Prop(text, "created_at"));
            Assert.Equal(Prop(Warm, "written_by"), Prop(text, "written_by"));
        }
    }

    [Fact]
    public void Patching_does_not_mutate_the_parsed_record()
    {
        var parsed = Load(Warm);
        var patch = new RecordPatch(Content: new ContentPatch("x", EntryType.Decision, Stamp(Later), Source: Change<SourceTool>.Clear));
        var first = RecordPatcher.Patch(parsed, patch).Bytes;
        var second = RecordPatcher.Patch(parsed, patch).Bytes;
        Assert.Equal(first, second);
        Assert.Equal(Warm, Encoding.UTF8.GetString(RecordWriter.Write(parsed).Bytes!));
        Assert.Equal("windsurf", parsed.UnknownSource);
    }

    [Fact]
    public void A_hand_edited_file_comes_out_canonical()
    {
        var messy = Minimal.Replace("\n", "\r\n").Replace("\"tags\": []", "\"tags\": [ \"b\" ,\"a\" ]").Replace("  ", "      ");
        var (text, _) = Apply(messy, new RecordPatch(Tags: new[] { "a", "b" }));
        Assert.Equal(Canonical(tagsJson: "[\n    \"a\",\n    \"b\"\n  ]"), text);
    }

    [Fact]
    public void A_patch_with_no_content_part_corrects_a_hand_edited_updated_at()
    {
        var edited = Warm.Replace("\"updated_at\": \"2026-11-12T09:14:03.512Z\"", "\"updated_at\": \"2020-01-01T00:00:00.000Z\"");
        var (text, _) = Apply(edited, new RecordPatch(Tags: new[] { "a" }));
        Assert.Equal("\"updated_at\": \"2026-11-12T09:14:03.512Z\"", Prop(text, "updated_at"));
    }

    [Fact]
    public void A_patch_that_cannot_be_encoded_is_refused()
    {
        var bad = new RecordPatch(Content: new ContentPatch("a" + (char)0xD800, EntryType.Decision, Stamp(Later)));
        Assert.Equal(PatchFailure.NotEncodable, RecordPatcher.Patch(Load(Warm), bad).Failure);
        Assert.Equal(PatchFailure.NotEncodable, RecordPatcher.Patch(Load(Warm), new RecordPatch(Tags: new[] { "" + (char)0xDC00 })).Failure);
    }

    [Fact]
    public void The_returned_record_is_the_one_the_bytes_hold()
    {
        var patches = new[]
        {
            new RecordPatch(Content: new ContentPatch("x", EntryType.Decision, Stamp(Later), Source: Change<SourceTool>.Set(SourceTool.Hermes))),
            new RecordPatch(Tags: new[] { "b", "a" }),
            new RecordPatch(Sticky: new StickyPatch(true, Stamp(Later), Stamp(Later2))),
            new RecordPatch(Tier: new TierPatch(Tier.Cold, Stamp(Later))),
            new RecordPatch(Sticky: new StickyPatch(false, Stamp(Later)), Tier: new TierPatch(Tier.Cold, Stamp(Later2))),
        };
        foreach (var patch in patches)
        {
            var result = RecordPatcher.Patch(Load(Warm), patch);
            var reparsed = RecordReader.Parse(result.Bytes!, RecordPath).Parsed!;
            Assert.Equal(reparsed.Record, result.Record);
        }
    }
}
