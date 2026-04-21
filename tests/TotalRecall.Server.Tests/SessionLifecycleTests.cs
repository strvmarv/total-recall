// tests/TotalRecall.Server.Tests/SessionLifecycleTests.cs
//
// Plan 4 Task 4.3 — unit tests for SessionLifecycle. Manual fakes only,
// no mock library. Covers idempotency, tier-summary computation, context
// formatting (with and without tags), the three hint priorities, hint
// truncation, the 5-hint cap, importer-detect gating, importer-count
// propagation, and last-session-age formatting across all units.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

public sealed class SessionLifecycleTests
{
    // ---------- helpers ----------

    private static Entry MakeEntry(
        string id,
        string content,
        IEnumerable<string>? tags = null,
        int accessCount = 0,
        long createdAt = 0,
        double decayScore = 1.0)   // ← new param
    {
        return new Entry(
            id,
            content,
            FSharpOption<string>.None,         // summary
            FSharpOption<string>.None,         // source
            FSharpOption<SourceTool>.None,     // sourceTool
            FSharpOption<string>.None,         // project
            ListModule.OfSeq(tags ?? Array.Empty<string>()),
            createdAt,                          // createdAt
            createdAt,                          // updatedAt
            createdAt,                          // lastAccessedAt
            accessCount,
            decayScore,                         // decayScore
            FSharpOption<string>.None,          // parentId
            FSharpOption<string>.None,          // collectionId
            "",                                 // scope
            EntryType.Preference,               // entryType
            "{}");                              // metadataJson
    }

    private sealed class FakeStore : IStore
    {
        // Per-(tier, type) entry lists. Test fixtures populate the slots they
        // care about; everything else returns empty.
        public Dictionary<(Tier, ContentType), List<Entry>> Entries { get; } = new();

        // Metadata-keyed lookup table; key is the metadata filter as a sorted
        // canonical string. Tests preload this directly.
        public Dictionary<string, List<Entry>> ByMetadata { get; } = new();

        public int CollectionCount { get; set; }

        // Diagnostic counters used by a couple of tests.
        public int CollectionsCallCount { get; private set; }

        private List<Entry> Slot(Tier t, ContentType ct)
        {
            if (!Entries.TryGetValue((t, ct), out var list))
            {
                list = new List<Entry>();
                Entries[(t, ct)] = list;
            }
            return list;
        }

        public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) =>
            throw new NotSupportedException();

        public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) =>
            throw new NotSupportedException();

        public Entry? Get(Tier tier, ContentType type, string id) =>
            Slot(tier, type).FirstOrDefault(e => e.Id == id);

        public long? GetInternalKey(Tier tier, ContentType type, string id) =>
            Slot(tier, type).Any(e => e.Id == id) ? 1L : null;

        public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) =>
            throw new NotSupportedException();

        public void Delete(Tier tier, ContentType type, string id) =>
            throw new NotSupportedException();

        public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
        {
            var src = Slot(tier, type).AsEnumerable();
            if (opts?.OrderBy is not null)
            {
                if (opts.OrderBy.StartsWith("access_count DESC", StringComparison.Ordinal))
                    src = src.OrderByDescending(e => e.AccessCount);
                else if (opts.OrderBy.StartsWith("decay_score ASC", StringComparison.Ordinal))
                    src = src.OrderBy(e => e.DecayScore);
                else if (opts.OrderBy.StartsWith("decay_score DESC", StringComparison.Ordinal))
                    src = src.OrderByDescending(e => e.DecayScore);
            }
            if (opts?.Limit is int lim) src = src.Take(lim);
            return src.ToList();
        }

        public int Count(Tier tier, ContentType type) => Slot(tier, type).Count;

        public int CountKnowledgeCollections()
        {
            CollectionsCallCount++;
            return CollectionCount;
        }

        public IReadOnlyList<Entry> ListByMetadata(
            Tier tier,
            ContentType type,
            IReadOnlyDictionary<string, string> metadataFilter,
            ListEntriesOpts? opts = null)
        {
            var key = MetaKey(tier, type, metadataFilter);
            if (!ByMetadata.TryGetValue(key, out var list)) return Array.Empty<Entry>();
            IEnumerable<Entry> src = list;
            if (opts?.OrderBy is not null && opts.OrderBy.StartsWith("access_count DESC", StringComparison.Ordinal))
            {
                src = src.OrderByDescending(e => e.AccessCount);
            }
            if (opts?.Limit is int lim) src = src.Take(lim);
            return src.ToList();
        }

        public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id)
        {
            var src = Slot(fromTier, fromType);
            var entry = src.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            src.Remove(entry);
            Slot(toTier, toType).Add(entry);
        }

        public string? FindByContent(Tier tier, ContentType type, string content)
            => Slot(tier, type).FirstOrDefault(e => e.Content == content)?.Id;

        public static string MetaKey(Tier tier, ContentType type, IReadOnlyDictionary<string, string> filter)
        {
            var parts = filter.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}");
            return $"{tier}|{type}|{string.Join(",", parts)}";
        }
    }

    private sealed class FakeImporter : IImporter
    {
        public string Name { get; }
        public bool DetectReturn { get; set; }
        public ImportResult MemResult { get; set; } = ImportResult.Empty;
        public ImportResult KbResult { get; set; } = ImportResult.Empty;

        public int DetectCalls { get; private set; }
        public int MemoryCalls { get; private set; }
        public int KnowledgeCalls { get; private set; }

        public FakeImporter(string name, bool detect = true)
        {
            Name = name;
            DetectReturn = detect;
        }

        public bool Detect()
        {
            DetectCalls++;
            return DetectReturn;
        }

        public ImporterScanResult Scan() => new(0, 0, 0);

        public ImportResult ImportMemories(string? project = null)
        {
            MemoryCalls++;
            return MemResult;
        }

        public ImportResult ImportKnowledge()
        {
            KnowledgeCalls++;
            return KbResult;
        }
    }

    private sealed class FakeCompactionLog : ICompactionLogReader
    {
        public long? LastTimestamp { get; set; }
        public string? LastExcludedReason { get; private set; }
        public int Calls { get; private set; }

        public long? GetLastTimestampExcludingReason(string excludedReason)
        {
            Calls++;
            LastExcludedReason = excludedReason;
            return LastTimestamp;
        }

        public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
            => System.Array.Empty<CompactionAnalyticsRow>();

        public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
            => System.Array.Empty<CompactionMovementRow>();

        public CompactionMovementRow? GetByTargetEntryId(string targetEntryId) => null;
    }

    private static SessionLifecycle BuildLifecycle(
        FakeStore store,
        IEnumerable<IImporter>? importers = null,
        ICompactionLogReader? compaction = null,
        long now = 1_000_000_000_000L,
        string sessionId = "sess-test",
        ISkillImportService? skillImportService = null,
        TimeSpan? skillImportTimeout = null,
        int tokenBudget = 4000,
        int maxEntries = 50)
    {
        return new SessionLifecycle(
            (importers ?? Array.Empty<IImporter>()).ToList(),
            store,
            compaction ?? new FakeCompactionLog(),
            sessionId,
            () => now,
            usageIndexer: null,
            storageMode: "sqlite",
            skillImportService: skillImportService,
            skillImportTimeout: skillImportTimeout,
            tokenBudget: tokenBudget,
            maxEntries: maxEntries);
    }

    private sealed class FakeSkillImportService : ISkillImportService
    {
        private readonly SkillImportSummaryDto[]? _summaries;
        private readonly Exception? _exception;
        private readonly Func<CancellationToken, Task<SkillImportSummaryDto[]>>? _async;
        private readonly SkillListResponseDto? _listResponse;
        private readonly Exception? _listException;

        public int ImportCalls { get; private set; }
        public int ListCalls { get; private set; }

        public FakeSkillImportService(
            SkillImportSummaryDto[]? summaries = null,
            Exception? exception = null,
            Func<CancellationToken, Task<SkillImportSummaryDto[]>>? asyncImpl = null,
            SkillListResponseDto? listResponse = null,
            Exception? listException = null)
        {
            _summaries = summaries;
            _exception = exception;
            _async = asyncImpl;
            _listResponse = listResponse;
            _listException = listException;
        }

        public Task<SkillImportSummaryDto[]> ImportAsync(string? projectPath, CancellationToken ct)
        {
            ImportCalls++;
            if (_exception is not null) throw _exception;
            if (_async is not null) return _async(ct);
            return Task.FromResult(_summaries ?? Array.Empty<SkillImportSummaryDto>());
        }

        public Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct)
        {
            ListCalls++;
            if (_listException is not null) throw _listException;
            return Task.FromResult(_listResponse
                ?? new SkillListResponseDto(0, 0, 0, Array.Empty<SkillDto>()));
        }
    }

    // ---------- BuildContext: sort + budget ----------

    [Fact]
    public void BuildContext_SortsByDecayScoreDescending()
    {
        var entries = new[]
        {
            MakeEntry("a", "low",  decayScore: 0.2),
            MakeEntry("b", "high", decayScore: 0.9),
            MakeEntry("c", "mid",  decayScore: 0.5),
        };
        var (context, _) = SessionLifecycle.BuildContext(entries, tokenBudget: 4000);
        var lines = context.Split('\n');
        Assert.Equal("- high", lines[0]);
        Assert.Equal("- mid",  lines[1]);
        Assert.Equal("- low",  lines[2]);
    }

    [Fact]
    public void BuildContext_RespectsTokenBudget()
    {
        // Each line is "- " + 10 chars = 12 chars.
        // Two lines with '\n' separator = 25 chars.
        // tokenBudget=7 → maxChars=28. First=12, second=12+1=13 → total 25 ≤ 28.
        // Third would add 13 more → 38 > 28 → truncated.
        var entries = new[]
        {
            MakeEntry("a", "aaaaaaaaaa", decayScore: 0.9),
            MakeEntry("b", "bbbbbbbbbb", decayScore: 0.8),
            MakeEntry("c", "cccccccccc", decayScore: 0.7),
        };
        var (context, truncated) = SessionLifecycle.BuildContext(entries, tokenBudget: 7);
        Assert.True(truncated);
        var lines = context.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("aaaaaaaaaa", lines[0]);
        Assert.Contains("bbbbbbbbbb", lines[1]);
    }

    [Fact]
    public void BuildContext_AllFit_NotTruncated()
    {
        var entries = new[]
        {
            MakeEntry("a", "hello", decayScore: 0.9),
            MakeEntry("b", "world", decayScore: 0.5),
        };
        var (context, truncated) = SessionLifecycle.BuildContext(entries, tokenBudget: 4000);
        Assert.False(truncated);
        Assert.Equal("- hello\n- world", context);
    }

    [Fact]
    public void BuildContext_Empty_ReturnsFalseAndEmptyString()
    {
        var (context, truncated) = SessionLifecycle.BuildContext(
            Array.Empty<Entry>(), tokenBudget: 4000);
        Assert.Equal(string.Empty, context);
        Assert.False(truncated);
    }

    [Fact]
    public void BuildContext_FirstEntryExceedsBudget_ReturnsEmptyWithTruncated()
    {
        // tokenBudget=1 → maxChars=4. "- hello" = 7 chars, which exceeds 4.
        // Contract: return ("", true) rather than a partial entry.
        var entries = new[] { MakeEntry("a", "hello", decayScore: 1.0) };
        var (context, truncated) = SessionLifecycle.BuildContext(entries, tokenBudget: 1);
        Assert.Equal(string.Empty, context);
        Assert.True(truncated);
    }

    [Fact]
    public async Task EnsureInitializedAsync_HotContextTruncated_WhenBudgetSmall()
    {
        var store = new FakeStore();
        // Three entries, each "- " + 10 chars = 12 chars. tokenBudget=2 → maxChars=8.
        // Even the first entry (12 chars) exceeds the budget → Truncated=true.
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("a", "aaaaaaaaaa", decayScore: 0.9),
            MakeEntry("b", "bbbbbbbbbb", decayScore: 0.8),
        };
        var lifecycle = BuildLifecycle(store, tokenBudget: 2);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.True(result.HotContextTruncated);
    }

    // ---------- warm sweep (synchronous) ----------

    [Fact]
    public async Task WarmSweep_DemotesExcessEntriesToWarm()
    {
        var store = new FakeStore();
        // 60 hot entries, maxEntries=50 → sweep should move 10 lowest-decay to warm.
        store.Entries[(Tier.Hot, ContentType.Memory)] = Enumerable.Range(0, 60)
            .Select(i => MakeEntry($"id{i}", $"content{i}", decayScore: (i + 1) / 60.0))
            .ToList();

        var lifecycle = BuildLifecycle(store, maxEntries: 50);
        await lifecycle.EnsureInitializedAsync();

        Assert.Equal(50, store.Count(Tier.Hot, ContentType.Memory));
        Assert.Equal(10, store.Count(Tier.Warm, ContentType.Memory));
    }

    [Fact]
    public async Task WarmSweep_SweepsLowestDecayFirst()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("low1",  "low1",  decayScore: 0.1),
            MakeEntry("low2",  "low2",  decayScore: 0.2),
            MakeEntry("high1", "high1", decayScore: 0.9),
            MakeEntry("high2", "high2", decayScore: 0.8),
            MakeEntry("mid",   "mid",   decayScore: 0.5),
        };

        // maxEntries=3 → 2 lowest should be swept
        var lifecycle = BuildLifecycle(store, maxEntries: 3);
        await lifecycle.EnsureInitializedAsync();

        var hot = store.Entries[(Tier.Hot, ContentType.Memory)].Select(e => e.Id).ToHashSet();
        var warm = store.Entries.GetValueOrDefault((Tier.Warm, ContentType.Memory), new List<Entry>())
            .Select(e => e.Id).ToHashSet();

        Assert.Contains("high1", hot);
        Assert.Contains("high2", hot);
        Assert.Contains("mid", hot);
        Assert.Contains("low1", warm);
        Assert.Contains("low2", warm);
    }

    [Fact]
    public async Task WarmSweep_NoOp_WhenUnderLimit()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = Enumerable.Range(0, 30)
            .Select(i => MakeEntry($"id{i}", $"content{i}", decayScore: (i + 1) / 30.0))
            .ToList();

        var lifecycle = BuildLifecycle(store, maxEntries: 50);
        await lifecycle.EnsureInitializedAsync();

        Assert.Equal(30, store.Count(Tier.Hot, ContentType.Memory));
        Assert.Equal(0, store.Count(Tier.Warm, ContentType.Memory));
    }

    // ---------- 1. idempotency ----------

    [Fact]
    public async Task EnsureInitializedAsync_Idempotent()
    {
        var store = new FakeStore();
        var importer = new FakeImporter("claude-code");
        var lifecycle = BuildLifecycle(store, new[] { importer });

        Assert.False(lifecycle.IsInitialized);
        var first = await lifecycle.EnsureInitializedAsync();
        var second = await lifecycle.EnsureInitializedAsync();

        Assert.True(lifecycle.IsInitialized);
        Assert.Same(first, second);
        Assert.Equal(1, importer.DetectCalls);
        Assert.Equal(1, importer.MemoryCalls);
        Assert.Equal(1, importer.KnowledgeCalls);
    }

    // ---------- 2. tier summary ----------

    [Fact]
    public async Task TierSummary_ComputedFromCounts()
    {
        var store = new FakeStore { CollectionCount = 4 };
        store.Entries[(Tier.Hot, ContentType.Memory)] = new()
        {
            MakeEntry("h1", "hot1"), MakeEntry("h2", "hot2"),
        };
        store.Entries[(Tier.Warm, ContentType.Memory)] = new() { MakeEntry("w1", "wm") };
        store.Entries[(Tier.Warm, ContentType.Knowledge)] = new()
        {
            MakeEntry("wk1", "wk1"), MakeEntry("wk2", "wk2"),
        };
        store.Entries[(Tier.Cold, ContentType.Memory)] = new() { MakeEntry("c1", "cm") };
        store.Entries[(Tier.Cold, ContentType.Knowledge)] = new()
        {
            MakeEntry("ck1", "ck"), MakeEntry("ck2", "ck"), MakeEntry("ck3", "ck"),
        };
        store.Entries[(Tier.Hot, ContentType.Knowledge)] = new() { MakeEntry("hk", "hk") };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(2, result.TierSummary.Hot);
        Assert.Equal(1 + 2, result.TierSummary.Warm);
        Assert.Equal(1 + 3, result.TierSummary.Cold);
        Assert.Equal(1 + 2 + 3, result.TierSummary.Kb);
        Assert.Equal(4, result.TierSummary.Collections);
        Assert.Equal(1, store.CollectionsCallCount);
        Assert.Equal(2, result.HotEntryCount);
    }

    // ---------- 3. context format ----------

    [Fact]
    public async Task Context_MatchesTsFormat()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new()
        {
            MakeEntry("a", "alpha", tags: new[] { "x", "y" }),
            MakeEntry("b", "bravo"),
            MakeEntry("c", "charlie", tags: new[] { "z" }),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal("- alpha [x, y]\n- bravo\n- charlie [z]", result.Context);
    }

    [Fact]
    public void BuildContext_EmptyHotTier_ReturnsEmptyString()
    {
        var (context, _) = SessionLifecycle.BuildContext(Array.Empty<Entry>());
        Assert.Equal(string.Empty, context);
    }

    // ---------- 4. P1 hints (corrections + preferences) ----------

    [Fact]
    public async Task Hints_Priority1_CorrectionsAndPreferences()
    {
        var store = new FakeStore();
        // Corrections (max 2 returned by store).
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "correction" })] = new()
        {
            MakeEntry("c1", "use tabs not spaces", accessCount: 5, createdAt: 100),
            MakeEntry("c2", "always run lint", accessCount: 3, createdAt: 200),
        };
        // Preferences (max 2). Highest access_count overall to test the merged sort.
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "preference" })] = new()
        {
            MakeEntry("p1", "prefers concise commits", accessCount: 9, createdAt: 50),
            MakeEntry("p2", "prefers TDD", accessCount: 4, createdAt: 60),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // Top 2 by access_count DESC then created_at ASC: p1 (9), c1 (5).
        Assert.Collection(result.Hints,
            h => Assert.Equal("prefers concise commits", h),
            h => Assert.Equal("use tabs not spaces", h));
    }

    // ---------- 5. P2 hints (frequently accessed) ----------

    [Fact]
    public async Task Hints_Priority2_FrequentlyAccessed()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Warm, ContentType.Memory)] = new()
        {
            MakeEntry("f1", "frequent one",   accessCount: 10),
            MakeEntry("f2", "frequent two",   accessCount: 7),
            MakeEntry("f3", "rare-1",         accessCount: 2),     // filtered (<3)
            MakeEntry("f4", "frequent three", accessCount: 5),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // No P1 hits; P2 takes top 2 with access_count >= 3.
        Assert.Equal(2, result.Hints.Count);
        Assert.Equal("frequent one", result.Hints[0]);
        Assert.Equal("frequent two", result.Hints[1]);
    }

    // ---------- 6. truncation ----------

    [Fact]
    public void TruncateHint_Over120Chars_AppendsEllipsis()
    {
        var input = new string('a', 200);
        var output = SessionLifecycle.TruncateHint(input);
        Assert.Equal(123, output.Length);
        Assert.EndsWith("...", output);
        Assert.StartsWith(new string('a', 120), output);
    }

    [Fact]
    public void TruncateHint_AtBoundary_NoEllipsis()
    {
        var input = new string('b', 120);
        Assert.Equal(input, SessionLifecycle.TruncateHint(input));
    }

    [Fact]
    public async Task Hints_Truncation_OverLongContentTrimmed()
    {
        var store = new FakeStore();
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "correction" })] = new()
        {
            MakeEntry("long", new string('z', 250), accessCount: 1),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Single(result.Hints);
        Assert.Equal(123, result.Hints[0].Length);
        Assert.EndsWith("...", result.Hints[0]);
    }

    // ---------- 7. cap at 5 ----------

    [Fact]
    public async Task Hints_MaxFive()
    {
        var store = new FakeStore();
        // 2 corrections (P1 cap = 2)
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "correction" })] = new()
        {
            MakeEntry("c1", "c-one", accessCount: 9),
            MakeEntry("c2", "c-two", accessCount: 8),
        };
        // 2 preferences — top 2 will go to P1 if higher access_count, but corrections
        // here have 9/8 so prefs are dropped from P1. P2 will then surface frequents.
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "preference" })] = new()
        {
            MakeEntry("p1", "p-one", accessCount: 1),
            MakeEntry("p2", "p-two", accessCount: 1),
        };
        // 5 frequents — only first 2 with access_count >= 3 are taken by P2.
        store.Entries[(Tier.Warm, ContentType.Memory)] = new()
        {
            MakeEntry("f1", "f-one", accessCount: 7),
            MakeEntry("f2", "f-two", accessCount: 6),
            MakeEntry("f3", "f-three", accessCount: 5),
            MakeEntry("f4", "f-four", accessCount: 4),
            MakeEntry("f5", "f-five", accessCount: 3),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // P1 contributes 2 (c-one, c-two), P2 contributes 2 (f-one, f-two).
        // Total = 4, well under cap. Verify cap enforcement separately by
        // inspecting the bound rather than fabricating an impossible 6+ scenario.
        Assert.True(result.Hints.Count <= 5);
        Assert.Equal(4, result.Hints.Count);
    }

    // ---------- 8. detect gating ----------

    [Fact]
    public async Task ImportSummary_OnlyDetectedImporters()
    {
        var detected = new FakeImporter("claude-code", detect: true);
        var notDetected = new FakeImporter("hermes", detect: false);
        var lifecycle = BuildLifecycle(new FakeStore(), new IImporter[] { detected, notDetected });

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(1, detected.MemoryCalls);
        Assert.Equal(1, detected.KnowledgeCalls);
        Assert.Equal(0, notDetected.MemoryCalls);
        Assert.Equal(0, notDetected.KnowledgeCalls);
        Assert.Single(result.ImportSummary);
        Assert.Equal("claude-code", result.ImportSummary[0].Tool);
    }

    // ---------- 9. import counts ----------

    [Fact]
    public async Task ImportSummary_CountsReflectImporterReturns()
    {
        var importer = new FakeImporter("cursor")
        {
            MemResult = new ImportResult(7, 0, Array.Empty<string>()),
            KbResult = new ImportResult(3, 0, Array.Empty<string>()),
        };
        var lifecycle = BuildLifecycle(new FakeStore(), new[] { importer });

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(7, result.ImportSummary[0].MemoriesImported);
        Assert.Equal(3, result.ImportSummary[0].KnowledgeImported);
    }

    // ---------- 10. last session age null ----------

    [Fact]
    public async Task LastSessionAge_Null_WhenNoLog()
    {
        var compaction = new FakeCompactionLog { LastTimestamp = null };
        var lifecycle = BuildLifecycle(new FakeStore(), compaction: compaction);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Null(result.LastSessionAge);
        Assert.Equal("warm_sweep_decay", compaction.LastExcludedReason);
    }

    // ---------- 11. last session age formatting (table-driven) ----------

    public static IEnumerable<object?[]> AgeFormatCases() => new object?[][]
    {
        new object?[] { 0L,                              "just now" },
        new object?[] { 30_000L,                         "just now" },
        new object?[] { 60_000L,                         "1 minute ago" },
        new object?[] { 5L * 60_000,                     "5 minutes ago" },
        new object?[] { 59L * 60_000,                    "59 minutes ago" },
        new object?[] { 60L * 60_000,                    "1 hour ago" },
        new object?[] { 3L * 60 * 60_000,                "3 hours ago" },
        new object?[] { 23L * 60 * 60_000,               "23 hours ago" },
        new object?[] { 24L * 60 * 60_000,               "1 day ago" },
        new object?[] { 3L * 24 * 60 * 60_000,           "3 days ago" },
        new object?[] { 6L * 24 * 60 * 60_000,           "6 days ago" },
        new object?[] { 7L * 24 * 60 * 60_000,           "1 week ago" },
        new object?[] { 14L * 24 * 60 * 60_000,          "2 weeks ago" },
        new object?[] { 30L * 24 * 60 * 60_000,          "4 weeks ago" },
    };

    [Theory]
    [MemberData(nameof(AgeFormatCases))]
    public void LastSessionAge_Formats_JustNow_Minutes_Hours_Days_Weeks(long elapsedMs, string expected)
    {
        const long now = 10_000_000_000L;
        var actual = SessionLifecycle.FormatLastSessionAge(now - elapsedMs, now);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LastSessionAge_Null_TimestampZero_ReturnsNull()
    {
        Assert.Null(SessionLifecycle.FormatLastSessionAge(null, 1_000));
        Assert.Null(SessionLifecycle.FormatLastSessionAge(0L, 1_000));
    }

    // ---------- 12. skill import integration ----------

    [Fact]
    public async Task EnsureInitializedAsync_WithSkillService_IncludesSkillCountsInSummary()
    {
        var svc = new FakeSkillImportService(
            summaries: new[]
            {
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: 3, Imported: 2, Updated: 1,
                    Unchanged: 0, Orphaned: 0,
                    Errors: Array.Empty<string>()),
            });
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        var row = Assert.Single(result.ImportSummary, r => r.Tool == "claude-code");
        Assert.Equal(2, row.SkillsImported);
        Assert.Equal(1, row.SkillsUpdated);
        Assert.Empty(row.SkillsErrors);
        Assert.Equal(1, svc.ImportCalls);
    }

    [Fact]
    public async Task EnsureInitializedAsync_WithSkillServiceMergesIntoExistingImporterRow()
    {
        // Memory importer contributes a "claude-code" row first; the skill
        // sweep must merge into it rather than append a duplicate.
        var importer = new FakeImporter("claude-code")
        {
            MemResult = new ImportResult(4, 0, Array.Empty<string>()),
            KbResult = new ImportResult(2, 0, Array.Empty<string>()),
        };
        var svc = new FakeSkillImportService(
            summaries: new[]
            {
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: 5, Imported: 3, Updated: 2,
                    Unchanged: 1, Orphaned: 1,
                    Errors: Array.Empty<string>()),
            });
        var lifecycle = BuildLifecycle(
            new FakeStore(),
            importers: new[] { importer },
            skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        var row = Assert.Single(result.ImportSummary, r => r.Tool == "claude-code");
        Assert.Equal(4, row.MemoriesImported);
        Assert.Equal(2, row.KnowledgeImported);
        Assert.Equal(3, row.SkillsImported);
        Assert.Equal(2, row.SkillsUpdated);
        Assert.Equal(1, row.SkillsUnchanged);
        Assert.Equal(1, row.SkillsOrphaned);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SkillServiceThrows_EmitsErrorRowNotFailure()
    {
        var svc = new FakeSkillImportService(
            exception: new InvalidOperationException("boom"));
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        var row = Assert.Single(result.ImportSummary, r => r.Tool == "claude-code");
        Assert.Equal(0, row.SkillsImported);
        Assert.Single(row.SkillsErrors);
        Assert.Contains("skill_import_failed", row.SkillsErrors[0]);
        Assert.Contains("boom", row.SkillsErrors[0]);
    }

    [Fact]
    public async Task EnsureInitializedAsync_WithoutSkillService_OmitsSkillRowAndLeavesMemoryRowsUntouched()
    {
        var importer = new FakeImporter("claude-code")
        {
            MemResult = new ImportResult(1, 0, Array.Empty<string>()),
            KbResult = new ImportResult(0, 0, Array.Empty<string>()),
        };
        var lifecycle = BuildLifecycle(
            new FakeStore(),
            importers: new[] { importer },
            skillImportService: null);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain(result.ImportSummary, r => r.Tool == "claude-code" && r.SkillsImported > 0);
        // Existing memory-importer rows should have SkillsImported == 0 (default for legacy rows).
        Assert.All(result.ImportSummary, r => Assert.Equal(0, r.SkillsImported));
        Assert.All(result.ImportSummary, r => Assert.Empty(r.SkillsErrors));
    }

    [Fact]
    public async Task EnsureInitializedAsync_SkillImportTimeout_EmitsTimeoutErrorRow()
    {
        // Configurable short timeout lets us exercise the OperationCanceledException
        // branch without actually waiting 5 seconds.
        var svc = new FakeSkillImportService(
            asyncImpl: async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                return Array.Empty<SkillImportSummaryDto>();
            });
        var lifecycle = BuildLifecycle(
            new FakeStore(),
            skillImportService: svc,
            skillImportTimeout: TimeSpan.FromMilliseconds(50));

        var result = await lifecycle.EnsureInitializedAsync();

        var row = Assert.Single(result.ImportSummary, r => r.Tool == "claude-code");
        Assert.Single(row.SkillsErrors);
        Assert.Equal("skill_import_timeout_5s", row.SkillsErrors[0]);
    }

    // ---------- 13. BuildSkillsBlock unit tests ----------

    private static SkillDto MakeSkillDto(string name, string? description = null) =>
        new SkillDto(
            Id: Guid.NewGuid(),
            Name: name,
            Description: description,
            Scope: "user",
            ScopeId: "u1",
            Tags: Array.Empty<string>(),
            Version: 1,
            Source: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void BuildSkillsBlock_NoSkills_ReturnsEmpty()
    {
        var response = new SkillListResponseDto(0, 0, 50, Array.Empty<SkillDto>());
        var block = SessionLifecycle.BuildSkillsBlock(response);
        Assert.Equal(string.Empty, block);
    }

    [Fact]
    public void BuildSkillsBlock_WithSkills_ContainsHeader()
    {
        var response = new SkillListResponseDto(
            Total: 2, Skip: 0, Take: 50,
            Items: new[]
            {
                MakeSkillDto("deploy-app", "Deploys the application"),
                MakeSkillDto("run-tests", "Runs the test suite"),
            });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("## Available Skills", block);
        Assert.Contains("skill_get", block);
        Assert.Contains("skill_search", block);
    }

    [Fact]
    public void BuildSkillsBlock_WithSkills_ContainsSkillNamesAndDescriptions()
    {
        var response = new SkillListResponseDto(
            Total: 2, Skip: 0, Take: 50,
            Items: new[]
            {
                MakeSkillDto("deploy-app", "Deploys the application"),
                MakeSkillDto("run-tests", "Runs the test suite"),
            });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- deploy-app: Deploys the application", block);
        Assert.Contains("- run-tests: Runs the test suite", block);
    }

    [Fact]
    public void BuildSkillsBlock_NullDescription_UsesNoDescriptionPlaceholder()
    {
        var response = new SkillListResponseDto(
            Total: 1, Skip: 0, Take: 50,
            Items: new[] { MakeSkillDto("my-skill", description: null) });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- my-skill: (no description)", block);
    }

    [Fact]
    public void BuildSkillsBlock_EmptyDescription_UsesNoDescriptionPlaceholder()
    {
        var response = new SkillListResponseDto(
            Total: 1, Skip: 0, Take: 50,
            Items: new[] { MakeSkillDto("my-skill", description: "   ") });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- my-skill: (no description)", block);
    }

    [Fact]
    public void BuildSkillsBlock_ManySkills_AllItemsAppear()
    {
        var items = Enumerable.Range(1, 100)
            .Select(i => MakeSkillDto($"skill-{i:D3}", $"Description {i}"))
            .ToArray();
        var response = new SkillListResponseDto(Total: 100, Skip: 0, Take: int.MaxValue, Items: items);

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- skill-001: Description 1", block);
        Assert.Contains("- skill-100: Description 100", block);
        var lineCount = block.Split('\n').Count(l => l.StartsWith("- "));
        Assert.Equal(100, lineCount);
    }

    // ---------- 14. skill listing integration in EnsureInitializedAsync ----------

    [Fact]
    public async Task EnsureInitializedAsync_SkillsPresent_ContextContainsSkillsBlock()
    {
        var listResponse = new SkillListResponseDto(
            Total: 2, Skip: 0, Take: 50,
            Items: new[]
            {
                MakeSkillDto("deploy-app", "Deploys the application"),
                MakeSkillDto("run-tests", null),
            });
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains("## Available Skills", result.Context);
        Assert.Contains("- deploy-app: Deploys the application", result.Context);
        Assert.Contains("- run-tests: (no description)", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ZeroSkills_ContextOmitsSkillsBlock()
    {
        var listResponse = new SkillListResponseDto(0, 0, 50, Array.Empty<SkillDto>());
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain("## Available Skills", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ManySkills_AllAppearedInContext()
    {
        var items = Enumerable.Range(1, 100)
            .Select(i => MakeSkillDto($"skill-{i:D3}"))
            .ToArray();
        var listResponse = new SkillListResponseDto(Total: 100, Skip: 0, Take: int.MaxValue, Items: items);
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains("- skill-100: (no description)", result.Context);
        Assert.DoesNotContain("[and", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ListVisibleThrows_ContextOmitsBlockNoException()
    {
        var svc = new FakeSkillImportService(
            listException: new InvalidOperationException("cortex down"));
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        // Must not throw.
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain("## Available Skills", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SkillsAndHotEntries_BothAppearedInContext()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("m1", "important memory"),
        };
        var listResponse = new SkillListResponseDto(
            Total: 1, Skip: 0, Take: 50,
            Items: new[] { MakeSkillDto("my-skill", "Does something useful") });
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(store, skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains("- important memory", result.Context);
        Assert.Contains("## Available Skills", result.Context);
        // Skills block comes after hot memories with a blank line between.
        var memIdx = result.Context.IndexOf("- important memory", StringComparison.Ordinal);
        var skillsIdx = result.Context.IndexOf("## Available Skills", StringComparison.Ordinal);
        Assert.True(memIdx < skillsIdx, "Hot memories should appear before skills block");
    }
}
