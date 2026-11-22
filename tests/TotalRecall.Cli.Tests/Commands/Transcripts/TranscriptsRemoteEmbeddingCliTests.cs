using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

/// <summary>
/// The remote-provider rule, the dimension refusals, the model readiness and a live lease through the three commands
/// a person runs (<c>backfill</c>, <c>embed</c>, <c>status</c>), over one archive in the main database and the Claude
/// Code fixture home (search the archive, spec section 8, tests 8 and 10, and section 4). The embedder the commands
/// inspect is a <see cref="RecordingEmbedder"/>, so every text that would reach a provider is recorded; no model.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class TranscriptsRemoteEmbeddingCliTests : IDisposable
{
    private const string RemoteOff = "words only: the embedding provider is remote and allow_remote_embedding is off";
    private const string NotReady = "words only: embedding model not ready (model.onnx is missing)";
    private const string Dim1536 = "words only: the provider's vectors have 1536 dimensions; the archive holds 384";
    private const string Dim768 = "words only: the provider's vectors have 768 dimensions; the archive holds 384";

    private readonly TranscriptsHarness _h = new();
    private readonly ClaudeFixtureHome _home = new();
    private readonly TextWriter _stderr = Console.Error;

    public TranscriptsRemoteEmbeddingCliTests()
    {
        _h.Interactive = false; // none of the three is terminal-gated
        Console.SetError(new StringWriter()); // the refused runs log through stderr
    }

    public void Dispose()
    {
        Console.SetError(_stderr);
        _h.Dispose();
        _home.Dispose();
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private string Cwd => Path.Combine(_home.Root, "work", "proj");

    /// <summary>The archive on, with <paramref name="extra"/> appended to <c>config.toml</c>, and two fixture sessions placed.</summary>
    private void Setup(string extra = "")
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n" + extra);
        _h.CreateArchive();
        _home.Place("decision", Cwd);
        _home.Place("compaction", Cwd);
    }

    private void Remote(bool allow) =>
        Setup($"allow_remote_embedding = {(allow ? "true" : "false")}\n[embedding]\nprovider = \"openai\"\n");

    /// <summary>Hands out <paramref name="embedder"/> with <paramref name="readiness"/> (ready by default) to every inspection.</summary>
    private void Use(IEmbedder embedder, EmbedderReadiness? readiness = null)
    {
        var d = embedder.Descriptor;
        _h.Inspector = _ => new EmbedderInspectionResult(
            embedder, new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions),
            readiness ?? new EmbedderReadiness(true, null), null);
    }

    private int Backfill() =>
        _h.Run(h => h.Backfill(_ => new ClaudeCodeAdapter(_home.ProjectsDir)));

    private int Embed() => _h.Run(h => h.Embed());

    private int Status() => _h.Run(h => h.Status());

    private string Out => _h.Out.ToString();
    private string Err => _h.Err.ToString();
    private long Chunks => _h.Scalar("SELECT count(*) FROM tx_chunk");
    private long Embedded => _h.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1");

    private string[] Lines => Out.Replace("\r", "").Split('\n');

    private string Line(string prefix)
    {
        var line = Lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(line is not null, $"no line starting with '{prefix}' in:\n{Out}");
        return line!;
    }

    // ---- the remote rule -----------------------------------------------------------------------------------------

    [Fact]
    public void RemoteWithoutOptIn_Backfill_Embed_Status_NoTextReachesTheProvider()
    {
        Remote(allow: false);
        var remote = new RecordingEmbedder("openai", 384);
        Use(remote);

        Assert.Equal(0, Backfill());
        var chunks = Chunks;
        Assert.True(chunks > 0);
        Assert.Contains($"  {chunks} chunks wait for embedding: {RemoteOff}", Out);

        Assert.Equal(1, Embed());
        Assert.Contains(RemoteOff, Err);

        Assert.Equal(0, Status());
        Assert.Equal("embedder:  " + RemoteOff, Line("embedder:"));
        Assert.Equal("search:    words only", Line("search:"));
        Assert.Equal("vectors:   none yet", Line("vectors:"));

        Assert.Empty(remote.Calls);     // no chunk, no probe
        Assert.Empty(_h.Inspections);   // not even built: rules 1 and 2 come from config
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void RemoteOptIn_1536Descriptor_Backfill_Embed_Exit1_RecorderEmpty()
    {
        Remote(allow: true);
        var remote = new RecordingEmbedder("openai", 1536);
        Use(remote);

        Assert.Equal(0, Backfill()); // the ingest's exit code; the embed step only reports
        Assert.Contains($"  {Chunks} chunks wait for embedding: {Dim1536}", Out);

        Assert.Equal(1, Embed());
        Assert.Contains(Dim1536, Err);

        Assert.Equal(0, Status());
        Assert.Equal("embedder:  " + Dim1536, Line("embedder:"));
        Assert.Equal("search:    words only", Line("search:"));

        Assert.Empty(remote.Calls); // rule 3 refuses before any call
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void RemoteOptIn_384DescriptorReturning768_OnlyTheProbeIsSent_StatusShowsTheRefusal()
    {
        Remote(allow: true);
        var remote = new RecordingEmbedder("openai", 384, 768);
        Use(remote);

        Assert.Equal(0, Backfill());
        Assert.Contains($"  {Chunks} chunks wait for embedding: {Dim768}", Out);
        Assert.Equal(new[] { EmbedProbe.Text }, remote.Calls.ToArray());

        Assert.Equal(1, Embed());
        Assert.Contains(Dim768, Err);
        Assert.Equal(new[] { EmbedProbe.Text, EmbedProbe.Text }, remote.Calls.ToArray()); // the probe again, never a chunk

        Assert.Equal(0, Status());
        var lastRun = Line("last embed run:");
        Assert.EndsWith(", embedded 0, " + Chunks + " remaining, stopped: " + Dim768, lastRun);
        Assert.Contains("the provider's vectors have 768 dimensions; the archive holds 384", lastRun);
        Assert.Equal(new[] { EmbedProbe.Text, EmbedProbe.Text }, remote.Calls.ToArray()); // status embeds nothing
        Assert.Equal(0, Embedded);
    }

    // ---- readiness -----------------------------------------------------------------------------------------------

    [Fact]
    public void ModelNotReady_Backfill_Embed_ChunksStayUnembedded()
    {
        Setup();
        var local = new RecordingEmbedder("local", 384);
        Use(local, new EmbedderReadiness(false, "model.onnx is missing"));

        Assert.Equal(0, Backfill());
        var chunks = Chunks;
        Assert.True(chunks > 0);
        Assert.Contains($"  {chunks} chunks wait for embedding: {NotReady}", Out);

        Assert.Equal(1, Embed());
        Assert.Contains(NotReady, Err);

        Assert.Equal(0, Status());
        Assert.Equal("embedder:  " + NotReady, Line("embedder:"));
        Assert.Equal("search:    words only", Line("search:"));

        Assert.Empty(local.Calls);
        Assert.Equal(0, Embedded);
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_chunk WHERE embed_attempts > 0"));
    }

    // ---- the lease -----------------------------------------------------------------------------------------------

    /// <summary>An embedder that holds its first call (the other run's probe) until released.</summary>
    private sealed class HoldingEmbedder : IEmbedder
    {
        private int _first = 1;
        public EmbedderDescriptor Descriptor { get; } = new("local", "bge-small-en-v1.5", "rev1", 384);
        public ManualResetEventSlim Hold { get; } = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);

        public float[] Embed(string text)
        {
            if (Interlocked.Exchange(ref _first, 0) == 1)
            {
                Entered.Set();
                Hold.Wait(TimeSpan.FromSeconds(30));
            }
            var v = new float[384];
            v[text.Length % 384] = 1f;
            return v;
        }
    }

    [Fact]
    public async Task CliEmbed_WhileLeaseHeldByAnotherRun_Exits1()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");
        _h.CreateArchive();
        _h.SeedChunks(20);
        var mine = new RecordingEmbedder("local", 384);
        Use(mine);

        // Another run (a session_start pass of a server, say) holds the lease while it is inside its probe.
        var other = new HoldingEmbedder();
        var otherStore = _h.OpenArchive();
        Task<EmbedRun> otherRun;
        try
        {
            otherRun = Task.Run(() => new TranscriptEmbedWorker(otherStore, other)
                .Run(new EmbedGateResult(EmbedState.Ready, "ready"), new ArchiveFingerprint("local", "bge-small-en-v1.5", "rev1", 384),
                    new EmbedBudget(null, null), CancellationToken.None));
            Assert.True(other.Entered.Wait(TimeSpan.FromSeconds(30)), "the other run never reached its probe");

            Assert.Equal(1, Embed());
            Assert.Contains("another embed run is active; try again later", Err);
            Assert.Empty(mine.Calls);
            Assert.Equal(0, Embedded);
        }
        finally
        {
            other.Hold.Set();
        }

        var result = await otherRun.WaitAsync(TimeSpan.FromSeconds(30));
        otherStore.Dispose();
        Assert.Equal(EmbedStopReason.Done, result.StoppedBy);
        Assert.Equal(20, Embedded);           // the holder finished its work
        Assert.Empty(mine.Calls);
    }
}
