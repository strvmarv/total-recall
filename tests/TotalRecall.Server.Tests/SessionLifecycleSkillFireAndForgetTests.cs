// tests/TotalRecall.Server.Tests/SessionLifecycleSkillFireAndForgetTests.cs
//
// Task 16 — verify that a slow skill import does NOT block session_start.
// The production change fires ImportAsync as a background task; this test
// confirms EnsureInitializedAsync completes quickly even when ImportAsync
// would take 10+ seconds.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public sealed class SessionLifecycleSkillFireAndForgetTests
{
    // ---------- minimal fakes (scoped to this file) ----------

    private sealed class MinimalStore : IStore
    {
        public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) => throw new NotSupportedException();
        public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) => throw new NotSupportedException();
        public Entry? Get(Tier tier, ContentType type, string id) => null;
        public long? GetInternalKey(Tier tier, ContentType type, string id) => null;
        public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) => throw new NotSupportedException();
        public void Delete(Tier tier, ContentType type, string id) => throw new NotSupportedException();
        public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null) => Array.Empty<Entry>();
        public int Count(Tier tier, ContentType type) => 0;
        public int CountKnowledgeCollections() => 0;
        public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type, IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null) => Array.Empty<Entry>();
        public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) { }
        public string? FindByContent(Tier tier, ContentType type, string content) => null;
    }

    private sealed class MinimalCompactionLog : ICompactionLogReader
    {
        public long? GetLastTimestampExcludingReason(string excludedReason) => null;
        public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null) => Array.Empty<CompactionAnalyticsRow>();
        public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit) => Array.Empty<CompactionMovementRow>();
        public CompactionMovementRow? GetByTargetEntryId(string targetEntryId) => null;
    }

    /// <summary>
    /// SlowImporter blocks for 10 seconds on ImportAsync — long enough to fail
    /// the 3-second threshold if the session lifecycle awaits it on the critical path.
    /// </summary>
    private sealed class SlowImporter : ISkillImportService
    {
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

        public Task<SkillImportSummaryDto[]> ImportAsync(string? projectPath, CancellationToken ct)
            => Task.Delay(Delay, ct).ContinueWith(
                _ => Array.Empty<SkillImportSummaryDto>(),
                TaskContinuationOptions.NotOnCanceled);

        public Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct)
            => Task.FromResult(new SkillListResponseDto(0, 0, 50, Array.Empty<SkillDto>()));

        public Task<ClaudeCodeScanResult> ScanExtraDirsAsync(CancellationToken ct)
            => Task.FromResult(new ClaudeCodeScanResult(Array.Empty<ImportedSkill>(), Array.Empty<ScanError>()));
    }

    private static SessionLifecycle BuildLifecycle(ISkillImportService? skillImportService = null)
    {
        return new SessionLifecycle(
            importers: Array.Empty<IImporter>().ToList(),
            store: new MinimalStore(),
            compactionLog: new MinimalCompactionLog(),
            sessionId: "sess-fire-forget-test",
            nowMs: () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            usageIndexer: null,
            storageMode: "sqlite",
            skillImportService: skillImportService,
            skillImportTimeout: null,
            tokenBudget: 4000,
            maxEntries: 50);
    }

    // ---------- the key test ----------

    [Fact]
    public async Task EnsureInitializedAsync_WithSlowSkillImport_CompletesInUnder3Seconds()
    {
        // Arrange: a skill importer that takes 10 seconds to complete.
        var svc = new SlowImporter();
        var lifecycle = BuildLifecycle(skillImportService: svc);

        // Act: time how long EnsureInitializedAsync takes.
        var sw = Stopwatch.StartNew();
        var result = await lifecycle.EnsureInitializedAsync();
        sw.Stop();

        // Assert: session init must return in under 3 seconds even though the
        // skill import takes 10 seconds. The import runs in the background.
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(3),
            $"EnsureInitializedAsync took {sw.Elapsed.TotalSeconds:F2}s — expected < 3s. " +
            "Skill import must be fire-and-forget, not on the critical path.");

        // The result should be valid (non-null, session id matches).
        Assert.NotNull(result);
        Assert.Equal("sess-fire-forget-test", result.SessionId);
    }
}
