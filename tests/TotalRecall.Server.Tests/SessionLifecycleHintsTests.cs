// tests/TotalRecall.Server.Tests/SessionLifecycleHintsTests.cs
//
// Task 4 — unit tests for the hot_tier_compaction_suggested hint emitted
// by SessionLifecycle.GenerateHints when the hot-tier entry count is at
// or above the configured CompactionHintThreshold.

namespace TotalRecall.Server.Tests;

using System;
using TotalRecall.Core;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionLifecycleHintsTests
{
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
}
