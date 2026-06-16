// InsightsHandler contract tests.
//
// Near-duplicate grouping needs discriminating embeddings, so these tests use a
// REAL SqliteStore + VectorSearch against :memory: plus a deterministic test
// embedder (distinct content => near-orthogonal vectors; identical content =>
// identical vectors). The handler re-embeds each live-set entry's content, so
// seeding via InsertWithEmbedding with the same embedder yields a vec0 row that
// the handler's search re-discovers at score ~1.0 for duplicates.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Server.Tests;

public class InsightsHandlerTests
{
    // ---- deterministic embedder ------------------------------------------
    //
    // Hashes the content to pick ONE hot dimension out of 384 and sets it to
    // 1.0 (already L2-normalized). Identical content => identical vector
    // (cosine 1.0, app score 1 - L2^2 = 1.0). Distinct content => orthogonal
    // vectors (cosine 0, app score 1 - 2 = -1.0), comfortably below any
    // sensible nearDupThreshold.
    private sealed class HashEmbedder : IEmbedder
    {
        public EmbedderDescriptor Descriptor { get; } = new("test", "hash", "", 384);

        public float[] Embed(string text)
        {
            var v = new float[384];
            var idx = (int)((uint)StableHash(text) % 384);
            v[idx] = 1.0f;
            return v;
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 17;
                foreach (var c in s) h = h * 31 + c;
                return h;
            }
        }
    }

    private sealed class TestInsightsContext : IInsightsContext
    {
        private readonly MsSqliteConnection _conn;
        public TestInsightsContext(
            MsSqliteConnection conn, IStore store, IVectorSearch vec, IEmbedder embedder,
            double threshold, IReadOnlyList<RetrievalEventRow> events,
            IReadOnlyList<CandidateRow> candidates, int kbChunks)
        {
            _conn = conn;
            Store = store;
            Vectors = vec;
            Embedder = embedder;
            SimilarityThreshold = threshold;
            _events = events;
            _candidates = candidates;
            _kbChunks = kbChunks;
        }

        private readonly IReadOnlyList<RetrievalEventRow> _events;
        private readonly IReadOnlyList<CandidateRow> _candidates;
        private readonly int _kbChunks;

        public IStore Store { get; }
        public IVectorSearch Vectors { get; }
        public IEmbedder Embedder { get; }
        public double SimilarityThreshold { get; }
        public IReadOnlyList<RetrievalEventRow> GetEvents(int days) => _events;
        public IReadOnlyList<CandidateRow> ListPendingCandidates() => _candidates;
        public int TotalKnowledgeChunks() => _kbChunks;

        // The conn is owned by the test fixture, not the context; do not close it
        // here (the handler disposes the context after analysis).
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        public MsSqliteConnection Conn { get; }
        public SqliteStore Store { get; }
        public VectorSearch Vec { get; }
        public HashEmbedder Embedder { get; } = new();
        public double Threshold { get; set; } = 0.7;
        public List<RetrievalEventRow> Events { get; } = new();
        public List<CandidateRow> Candidates { get; } = new();
        public int KbChunks { get; set; }

        public Fixture()
        {
            Conn = SqliteConnection.Open(":memory:");
            MigrationRunner.RunMigrations(Conn);
            Store = new SqliteStore(Conn);
            Vec = new VectorSearch(Conn);
        }

        public string Seed(Tier tier, string content, int accessCount = 0,
            EntryType? entryType = null, long createdAt = 1000)
        {
            var id = Store.InsertWithEmbedding(
                tier, ContentType.Memory,
                new InsertEntryOpts(content, EntryType: entryType ?? EntryType.Imported),
                Embedder.Embed(content).AsMemory());
            if (accessCount > 0)
            {
                // TableName is internal to Infrastructure; replicate the memory-tier
                // naming convention inline (hot_memories / warm_memories / ...).
                var tierStr = tier.IsHot ? "hot" : tier.IsWarm ? "warm" : tier.IsCold ? "cold" : "pinned";
                using var cmd = Conn.CreateCommand();
                cmd.CommandText =
                    $"UPDATE {tierStr}_memories SET access_count = $ac, created_at = $ca WHERE id = $id";
                cmd.Parameters.AddWithValue("$ac", accessCount);
                cmd.Parameters.AddWithValue("$ca", createdAt);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            return id;
        }

        public InsightsContextProvider Provider() =>
            () => new TestInsightsContext(Conn, Store, Vec, Embedder, Threshold,
                Events, Candidates, KbChunks);

        public void Dispose() => Conn.Dispose();
    }

    private static RetrievalEventRow Event(double? topScore, bool? used, string query = "q")
        => new(
            Id: Guid.NewGuid().ToString(), Timestamp: 1000, SessionId: "s",
            QueryText: query, QuerySource: "test", QueryEmbedding: null,
            ResultsJson: "[]", ResultCount: 0, TopScore: topScore, TopTier: "warm",
            TopContentType: "memory", OutcomeUsed: used, OutcomeSignal: null,
            ConfigSnapshotId: "cfg", LatencyMs: 5, TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<JsonElement> RunAsync(Fixture fx, string? argsJson = null)
    {
        var handler = new InsightsHandler(fx.Provider());
        var args = argsJson is null ? (JsonElement?)null : Args(argsJson);
        var result = await handler.ExecuteAsync(args, CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
        return JsonDocument.Parse(result.Content[0].Text).RootElement.Clone();
    }

    // ---- name / schema ----------------------------------------------------

    [Fact]
    public void Name_Is_insights()
    {
        var handler = new InsightsHandler();
        Assert.Equal("insights", handler.Name);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }

    // ---- near-duplicates --------------------------------------------------

    [Fact]
    public async Task NearDuplicates_ClustersTwoDuplicates_ExcludesDistinct()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.7;
        fx.Seed(Tier.Warm, "the quick brown fox");
        fx.Seed(Tier.Warm, "the quick brown fox");   // exact dup of #1
        fx.Seed(Tier.Warm, "a completely unrelated memory about databases");

        var root = await RunAsync(fx, """{"nearDupThreshold":0.7}""");
        var groups = root.GetProperty("nearDuplicates");

        Assert.Equal(1, groups.GetArrayLength());      // size>=2 filter drops the singleton
        var group = groups[0];
        Assert.Equal(2, group.GetProperty("members").GetArrayLength());
        Assert.True(group.GetProperty("topScore").GetDouble() >= 0.7);
    }

    [Fact]
    public async Task NearDuplicates_SameTierClusters_NotCrossTier()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.7;
        // Two identical WARM memories + one identical HOT memory. Near-dup
        // detection is SAME-TIER (v1): the vec0 search is scoped to one tier,
        // so the hot copy never joins the warm pair even though its content is
        // identical. Result: a single cluster of size 2 (the two warm dups),
        // with the hot copy isolated in its own tier.
        fx.Seed(Tier.Warm, "transitive duplicate content");
        fx.Seed(Tier.Warm, "transitive duplicate content");
        fx.Seed(Tier.Hot, "transitive duplicate content");
        // A distinct warm entry that must NOT join.
        fx.Seed(Tier.Warm, "entirely different topic here");

        var root = await RunAsync(fx, """{"nearDupThreshold":0.7}""");
        var groups = root.GetProperty("nearDuplicates");

        Assert.Equal(1, groups.GetArrayLength());
        Assert.Equal(2, groups[0].GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task NearDuplicates_ThreeMemberCluster_TopScoreIsMaxPairwise()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.7;
        // Three near-duplicate memories in the SAME tier => one cluster of 3.
        //
        // The HashEmbedder is intentionally bimodal: identical content yields
        // identical vectors (app score ~1.0) and any distinct content yields an
        // orthogonal vector (app score ~-1.0). There is no intermediate band, so
        // "slightly varying content" cannot produce differing-but-above-threshold
        // pairwise scores with this test embedder — three exact dups are the only
        // way to land three members above the 0.7 threshold in one cluster. The
        // assertion that matters regardless of embedder shape is that the group's
        // topScore equals the maximum pairwise score across its members.
        fx.Seed(Tier.Warm, "alpha shared token");           // A
        fx.Seed(Tier.Warm, "alpha shared token");           // B == A
        fx.Seed(Tier.Warm, "alpha shared token");           // C == A == B
        // A distinct entry that must NOT join.
        fx.Seed(Tier.Warm, "completely different subject");

        var root = await RunAsync(fx, """{"nearDupThreshold":0.7}""");
        var groups = root.GetProperty("nearDuplicates");

        Assert.Equal(1, groups.GetArrayLength());
        var group = groups[0];
        Assert.Equal(3, group.GetProperty("members").GetArrayLength());

        // topScore must equal the maximum pairwise score among the three members
        // (per-member score is each member's best pairwise edge, so the cluster's
        // max member score IS the max pairwise score).
        var maxMemberScore = group.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("score").GetDouble())
            .Max();
        Assert.Equal(maxMemberScore, group.GetProperty("topScore").GetDouble(), 6);
        Assert.True(group.GetProperty("topScore").GetDouble() >= 0.7);
    }

    [Fact]
    public async Task NearDuplicates_MembersCarryCreatedAt()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.7;
        // accessCount>0 routes Seed through the UPDATE that also sets created_at,
        // so the two dups carry distinct, known timestamps the client can use to
        // pick the newest member.
        fx.Seed(Tier.Warm, "timestamped duplicate content", accessCount: 1, createdAt: 5000);
        fx.Seed(Tier.Warm, "timestamped duplicate content", accessCount: 1, createdAt: 9000);

        var root = await RunAsync(fx, """{"nearDupThreshold":0.7}""");
        var groups = root.GetProperty("nearDuplicates");
        Assert.Equal(1, groups.GetArrayLength());
        var members = groups[0].GetProperty("members");
        Assert.Equal(2, members.GetArrayLength());
        foreach (var m in members.EnumerateArray())
        {
            Assert.True(m.TryGetProperty("createdAt", out var ca));
            Assert.True(ca.GetInt64() > 0);
        }
        var stamps = members.EnumerateArray()
            .Select(m => m.GetProperty("createdAt").GetInt64())
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(new long[] { 5000, 9000 }, stamps);
    }

    [Fact]
    public async Task NearDuplicates_TransitiveWithinTier_CollapsesThree()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.7;
        fx.Seed(Tier.Warm, "same content three times");
        fx.Seed(Tier.Warm, "same content three times");
        fx.Seed(Tier.Warm, "same content three times");

        var root = await RunAsync(fx, """{"nearDupThreshold":0.7}""");
        var groups = root.GetProperty("nearDuplicates");
        Assert.Equal(1, groups.GetArrayLength());
        Assert.Equal(3, groups[0].GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task NearDuplicates_SkipsColdTier()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.7;
        fx.Seed(Tier.Cold, "archived duplicate content");
        fx.Seed(Tier.Cold, "archived duplicate content");

        var root = await RunAsync(fx, """{"nearDupThreshold":0.7}""");
        Assert.Equal(0, root.GetProperty("nearDuplicates").GetArrayLength());
    }

    // ---- pin candidates ---------------------------------------------------

    [Fact]
    public async Task PinCandidates_AppliesAccessThreshold_ExcludesPinned()
    {
        using var fx = new Fixture();
        fx.Seed(Tier.Warm, "hot memory", accessCount: 12);   // candidate
        fx.Seed(Tier.Warm, "rarely used", accessCount: 3);   // below threshold
        fx.Seed(Tier.Pinned, "already pinned", accessCount: 99); // pinned => excluded

        var root = await RunAsync(fx, """{"pinAccessThreshold":8}""");
        var candidates = root.GetProperty("pinCandidates");

        Assert.Equal(1, candidates.GetArrayLength());
        Assert.Equal(12, candidates[0].GetProperty("accessCount").GetInt32());
        Assert.Equal("warm", candidates[0].GetProperty("tier").GetString());
    }

    [Fact]
    public async Task PinCandidates_CapsToTen()
    {
        using var fx = new Fixture();
        for (int i = 0; i < 15; i++)
            fx.Seed(Tier.Warm, $"frequently accessed memory number {i}", accessCount: 10 + i);

        var root = await RunAsync(fx, """{"pinAccessThreshold":8}""");
        Assert.Equal(10, root.GetProperty("pinCandidates").GetArrayLength());
    }

    // ---- threshold curve --------------------------------------------------

    [Fact]
    public async Task ThresholdCurve_HasFivePoints_AndCurrent()
    {
        using var fx = new Fixture();
        fx.Threshold = 0.72;
        fx.Events.Add(Event(0.9, true));
        fx.Events.Add(Event(0.4, false));

        var root = await RunAsync(fx);
        var curve = root.GetProperty("thresholdCurve");

        Assert.Equal(0.72, curve.GetProperty("current").GetDouble());
        var points = curve.GetProperty("points");
        Assert.Equal(5, points.GetArrayLength());
        Assert.Equal(0.5, points[0].GetProperty("threshold").GetDouble());
        Assert.Equal(0.9, points[4].GetProperty("threshold").GetDouble());
        // each point carries hitRate / precision / mrr
        foreach (var p in points.EnumerateArray())
        {
            Assert.True(p.TryGetProperty("hitRate", out _));
            Assert.True(p.TryGetProperty("precision", out _));
            Assert.True(p.TryGetProperty("mrr", out _));
        }
    }

    // ---- retrieval gaps ---------------------------------------------------

    [Fact]
    public async Task RetrievalGaps_FromPendingCandidates_TopByFrequency()
    {
        using var fx = new Fixture();
        fx.Candidates.Add(new CandidateRow(
            "c1", "how to configure cortex", 0.3, null, null, 1, 2, 7, "pending"));
        fx.Candidates.Add(new CandidateRow(
            "c2", "rarely missed query", 0.2, null, null, 1, 2, 1, "pending"));

        var root = await RunAsync(fx);
        var gaps = root.GetProperty("retrievalGaps");
        Assert.True(gaps.GetArrayLength() >= 1);
        // highest times_seen first
        Assert.Equal("how to configure cortex", gaps[0].GetProperty("query").GetString());
        Assert.Equal(7, gaps[0].GetProperty("timesSeen").GetInt32());
    }

    // ---- health breakdown -------------------------------------------------

    [Fact]
    public async Task HealthBreakdown_ComponentMaxes_AndSubScoresSumToHealthScore()
    {
        using var fx = new Fixture();
        fx.KbChunks = 5;
        fx.Threshold = 0.5;
        fx.Events.Add(Event(0.9, true));
        fx.Events.Add(Event(0.8, true));
        fx.Seed(Tier.Warm, "a decision was made", entryType: EntryType.Decision);
        fx.Seed(Tier.Warm, "user preference noted", entryType: EntryType.Preference);

        var root = await RunAsync(fx);
        var bd = root.GetProperty("healthBreakdown");

        Assert.Equal(35, bd.GetProperty("retrieval").GetProperty("max").GetInt32());
        Assert.Equal(25, bd.GetProperty("capture").GetProperty("max").GetInt32());
        Assert.Equal(20, bd.GetProperty("pinned").GetProperty("max").GetInt32());
        Assert.Equal(20, bd.GetProperty("kb").GetProperty("max").GetInt32());

        var sum =
            bd.GetProperty("retrieval").GetProperty("score").GetInt32() +
            bd.GetProperty("capture").GetProperty("score").GetInt32() +
            bd.GetProperty("pinned").GetProperty("score").GetInt32() +
            bd.GetProperty("kb").GetProperty("score").GetInt32();
        Assert.Equal(sum, root.GetProperty("healthScore").GetInt32());

        // each component carries a human-readable detail string
        foreach (var name in new[] { "retrieval", "capture", "pinned", "kb" })
            Assert.False(string.IsNullOrEmpty(
                bd.GetProperty(name).GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task HealthBreakdown_NoEvents_RetrievalNeutral25()
    {
        using var fx = new Fixture();
        fx.KbChunks = 0; // kb floor 10
        var root = await RunAsync(fx);
        var bd = root.GetProperty("healthBreakdown");
        Assert.Equal(25, bd.GetProperty("retrieval").GetProperty("score").GetInt32());
        Assert.Equal(10, bd.GetProperty("kb").GetProperty("score").GetInt32());
        Assert.Equal(20, bd.GetProperty("pinned").GetProperty("score").GetInt32()); // 0 pinned
    }

    [Fact]
    public async Task HealthBreakdown_PinnedOverBudget_DegradesScore()
    {
        using var fx = new Fixture();
        for (int i = 0; i < 18; i++) fx.Seed(Tier.Pinned, $"pinned memory {i}");
        var root = await RunAsync(fx);
        var pinned = root.GetProperty("healthBreakdown").GetProperty("pinned");
        // 18 pinned => 20 - (18-15) = 17
        Assert.Equal(17, pinned.GetProperty("score").GetInt32());
    }

    // ---- args validation + roundtrip --------------------------------------

    [Fact]
    public async Task InvalidDays_Throws()
    {
        using var fx = new Fixture();
        var handler = new InsightsHandler(fx.Provider());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"days":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"days":"abc"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        using var fx = new Fixture();
        fx.KbChunks = 1;
        var handler = new InsightsHandler(fx.Provider());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.InsightsResultDto);
        Assert.NotNull(dto);
        Assert.NotNull(dto!.HealthBreakdown);
        Assert.Equal(5, dto.ThresholdCurve.Points.Length);
        Assert.InRange(dto.HealthScore, 0, 100);
    }
}
