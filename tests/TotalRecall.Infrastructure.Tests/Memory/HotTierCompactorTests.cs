// tests/TotalRecall.Infrastructure.Tests/Memory/HotTierCompactorTests.cs

using System;
using System.Collections.Generic;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public sealed class HotTierCompactorTests
{
    private static Entry MakeEntry(string id, long lastAccessedAt) =>
        new Entry(
            id, id,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            createdAt: 0, updatedAt: 0, lastAccessedAt: lastAccessedAt,
            accessCount: 0, decayScore: 1.0,
            FSharpOption<string>.None, FSharpOption<string>.None,
            scope: "", entryType: EntryType.Preference,
            metadataJson: "{}", timesInjected: 0);

    [Fact]
    public void Compact_PromotesStaleEntriesBelowThreshold()
    {
        var store = new InMemoryTestStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("stale", lastAccessedAt: 0));
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("fresh", lastAccessedAt: now));

        var result = HotTierCompactor.Compact(
            store, sessionId: "s1", nowMs: now,
            warmThreshold: 0.5, decayConstantHours: 168, compactionLog: null);

        Assert.Equal(1, result.Promoted);
        Assert.Equal(1, result.CarryForward);
        Assert.Equal(1, store.Count(Tier.Warm, ContentType.Memory));
        Assert.Equal(1, store.Count(Tier.Hot, ContentType.Memory));
    }

    [Fact]
    public void Compact_EmptyStore_PromotesNothing()
    {
        var store = new InMemoryTestStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = HotTierCompactor.Compact(
            store, sessionId: "s1", nowMs: now,
            warmThreshold: 0.5, decayConstantHours: 168, compactionLog: null);

        Assert.Equal(0, result.Promoted);
        Assert.Equal(0, result.CarryForward);
        Assert.Equal(0, store.Count(Tier.Warm, ContentType.Memory));
    }

    [Fact]
    public void Compact_AllFreshEntries_PromotesNothing()
    {
        var store = new InMemoryTestStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("fresh1", lastAccessedAt: now));
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("fresh2", lastAccessedAt: now));

        var result = HotTierCompactor.Compact(
            store, sessionId: "s1", nowMs: now,
            warmThreshold: 0.5, decayConstantHours: 168, compactionLog: null);

        Assert.Equal(0, result.Promoted);
        Assert.Equal(2, result.CarryForward);
        Assert.Equal(0, store.Count(Tier.Warm, ContentType.Memory));
        Assert.Equal(2, store.Count(Tier.Hot, ContentType.Memory));
    }
}
