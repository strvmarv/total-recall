// tests/TotalRecall.Server.Tests/SessionLifecycleHintsTests.cs
//
// Task 4 — unit tests for the hot_tier_compaction_suggested hint emitted
// by SessionLifecycle.GenerateHints when the hot-tier entry count is at
// or above the configured CompactionHintThreshold.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionLifecycleHintsTests
{
    private static Entry MakeEntry(string id) =>
        new(
            id, "c",
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 4, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Correction, "{}", 0);

    [Fact]
    public void GenerateHints_EmitsCompactionHint_WhenHotCountAtThreshold()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 5);

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        Assert.Contains(hints, h => h.Type == "hot_tier_compaction_suggested");
    }

    [Fact]
    public void GenerateHints_NoCompactionHint_BelowThreshold()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 1);

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        Assert.DoesNotContain(hints, h => h.Type == "hot_tier_compaction_suggested");
    }

    [Fact]
    public void GenerateHints_NoCompactionHint_JustBelowThreshold()
    {
        var store = new FakeStore();
        // threshold - 1: tightest off-by-one case that must NOT emit the hint.
        store.SeedCount(Tier.Hot, ContentType.Memory, 4);

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        Assert.DoesNotContain(hints, h => h.Type == "hot_tier_compaction_suggested");
    }

    [Fact]
    public void GenerateHints_NoCompactionHint_WhenThresholdIsZero()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 100);

        // threshold = 0 means disabled (default for existing callers)
        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 0);

        Assert.DoesNotContain(hints, h => h.Type == "hot_tier_compaction_suggested");
    }

    [Fact]
    public void GenerateHints_EmitsCompactionHint_AboveThreshold()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 10);

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        Assert.Contains(hints, h => h.Type == "hot_tier_compaction_suggested");
    }

    [Fact]
    public void GenerateHints_CompactionHint_HasCorrectShape()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 5);

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        var hint = Assert.Single(hints, h => h.Type == "hot_tier_compaction_suggested");
        Assert.Equal(2, hint.Priority);
        Assert.Equal("compact", hint.SuggestedAction);
    }

    [Fact]
    public void GenerateHints_CompactionHint_AppearsFirst()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 5);

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        Assert.NotEmpty(hints);
        Assert.Equal("hot_tier_compaction_suggested", hints[0].Type);
    }

    [Fact]
    public void GenerateHints_CompactionHint_AppearsFirst_AmongCompetingHints()
    {
        var store = new FakeStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 5);
        // Seed a warm correction so a lower-priority warm_promotion_candidate hint
        // is generated alongside the compaction hint — proving order under contention,
        // not just in a near-empty store.
        store.SeedListByMetadata(
            Tier.Warm, ContentType.Memory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["entry_type"] = "correction" },
            MakeEntry("warm-corr"));

        var hints = SessionLifecycle.GenerateHints(store, Array.Empty<string>(), compactionHintThreshold: 5);

        Assert.Contains(hints, h => h.Type == "warm_promotion_candidate");
        Assert.Equal("hot_tier_compaction_suggested", hints[0].Type);
    }
}
