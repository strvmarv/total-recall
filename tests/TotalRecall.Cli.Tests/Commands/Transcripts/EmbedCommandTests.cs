using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using TotalRecall.Cli.Commands.Transcripts;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class EmbedCommandTests : IDisposable
{
    private const string RemoteOff = "words only: the embedding provider is remote and allow_remote_embedding is off";

    private readonly TranscriptsHarness _h = new();

    public EmbedCommandTests() => _h.Interactive = false; // embed is not terminal-gated

    public void Dispose() => _h.Dispose();

    private int Embed(params string[] args) => _h.Run(h => h.Embed(), args);

    private int Embed(CancellationToken ct, params string[] args) => _h.Run(h => h.Embed(ct), args);

    private void EnableArchive(string extra = "")
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n" + extra);
        _h.CreateArchive();
    }

    private void Remote(bool allow, int dims = 384)
    {
        EnableArchive($"allow_remote_embedding = {(allow ? "true" : "false")}\n[embedding]\nprovider = \"openai\"\n");
        _h.Embedder.Descriptor = new EmbedderDescriptor("openai", "text-embedding-3-small", "", dims);
    }

    private long Embedded => _h.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1");

    private long Vectors => _h.Scalar("SELECT count(*) FROM tx_chunk_vec");

    private static int WithSilentStderr(Func<int> run)
    {
        var original = Console.Error;
        try
        {
            Console.SetError(new StringWriter());
            return run();
        }
        finally
        {
            Console.SetError(original);
        }
    }

    // ---- refusals before anything is read --------------------------------------------------------------------------

    [Fact]
    public void ArchiveOff_RefusesExit1_WithThePart2Text_NothingInspected()
    {
        _h.CreateArchive();
        _h.SeedChunks(2);

        Assert.Equal(1, Embed());
        Assert.Contains(
            "transcripts embed: the transcript archive is off; run 'total-recall transcripts enable' in a terminal to turn it on",
            _h.Err.ToString());
        Assert.Empty(_h.Inspections);
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void Postgres_RefusesExit1()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n[storage]\nmode = \"postgres\"\nconnection_string = \"Host=h\"\n");

        Assert.Equal(1, Embed());
        Assert.Contains("postgres", _h.Err.ToString());
        Assert.Empty(_h.Inspections);
    }

    [Fact]
    public void ArchiveAbsent_RefusesExit1_CreatesNothing()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");

        Assert.Equal(1, Embed());
        Assert.Contains("the archive tables do not exist", _h.Err.ToString());
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void UnknownArgument_Exit2_WithUsage()
    {
        EnableArchive();

        Assert.Equal(2, Embed("--all"));
        Assert.Contains("Usage: total-recall transcripts embed [--retry-failed]", _h.Err.ToString());
        Assert.Equal(2, Embed("everything"));
        Assert.Empty(_h.Inspections);
    }

    // ---- running ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NothingToEmbed_Exit0_SaysSo()
    {
        EnableArchive();

        Assert.Equal(0, Embed());
        Assert.Contains("nothing to embed", _h.Out.ToString());
        Assert.Empty(_h.Embedder.Texts);
    }

    [Fact]
    public void EmbedsAllWaitingChunks_PrintsProgressAndAClosingLine_Exit0()
    {
        EnableArchive();
        _h.SeedChunks(20);

        var code = Embed();

        Assert.Equal(0, code);
        Assert.Equal(20, Embedded);
        Assert.Equal(20, Vectors);
        var output = _h.Out.ToString();
        Assert.Contains("embedded 16 of 20 (4 remaining)", output);
        Assert.Contains("done: embedded 20 chunks", output);
        Assert.DoesNotContain("could not be embedded", output);
        Assert.Equal(1, _h.Embedder.Disposed);
    }

    [Fact]
    public void NotTerminalGated_NonInteractiveStdinWorks()
    {
        EnableArchive();
        _h.SeedChunks(1);
        _h.Interactive = false;

        Assert.Equal(0, Embed());
        Assert.Empty(_h.Prompts);
        Assert.Equal(1, Embedded);
    }

    [Fact]
    public void Progress_IsThrottledToOneLinePerFiveSeconds()
    {
        EnableArchive();
        _h.SeedChunks(48); // three batches; the clock never moves

        Assert.Equal(0, Embed());
        var lines = Regex.Matches(_h.Out.ToString(), @"embedded \d+ of 48 \(\d+ remaining\)");
        Assert.Single(lines);
        Assert.Equal("embedded 16 of 48 (32 remaining)", lines[0].Value);
    }

    [Fact]
    public void Progress_PrintsAfterEveryBatchOnceFiveSecondsPassed()
    {
        EnableArchive();
        _h.SeedChunks(48);
        _h.Embedder.OnEmbed = _ => _h.Time.Advance(TimeSpan.FromSeconds(1)); // 16 s per batch

        Assert.Equal(0, Embed());
        var lines = Regex.Matches(_h.Out.ToString(), @"embedded \d+ of 48 \(\d+ remaining\)").Select(m => m.Value).ToArray();
        Assert.Equal(
            ["embedded 16 of 48 (32 remaining)", "embedded 32 of 48 (16 remaining)", "embedded 48 of 48 (0 remaining)"],
            lines);
    }

    [Fact]
    public void Cancelled_StopsAfterTheCurrentBatch_Exit0()
    {
        EnableArchive();
        _h.SeedChunks(20);
        using var cts = new CancellationTokenSource();
        _h.Embedder.OnEmbed = text => { if (text == "chunk-0") cts.Cancel(); };

        var code = Embed(cts.Token);

        Assert.Equal(0, code);
        Assert.Equal(16, Embedded);
        Assert.Contains("interrupted: embedded 16 chunks, 4 remaining", _h.Out.ToString());
    }

    // ---- gate refusals ---------------------------------------------------------------------------------------------

    [Fact]
    public void RemoteWithoutOptIn_Exit1_PrintsTheState_NothingInspectedNoTextSent()
    {
        Remote(allow: false);
        _h.SeedChunks(3, prefix: "zqx-secret-");

        Assert.Equal(1, Embed());
        Assert.Contains(RemoteOff, _h.Err.ToString());
        Assert.Empty(_h.Inspections);
        Assert.Empty(_h.Embedder.Texts);
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void Dimension_Exit1_PrintsTheState_NoArchiveTextReachesTheRemote()
    {
        Remote(allow: true, dims: 768);
        _h.SeedChunks(3, prefix: "zqx-secret-");

        Assert.Equal(1, Embed());
        Assert.Contains("words only: the provider's vectors have 768 dimensions; the archive holds 384", _h.Err.ToString());
        Assert.DoesNotContain(_h.Embedder.Texts, t => t.Contains("zqx", StringComparison.Ordinal));
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void ProbeDimensionRefusal_Exit1_NoArchiveTextReachesTheRemote()
    {
        Remote(allow: true);
        _h.Embedder.Length = 1536; // the service ignored the requested dimensions
        _h.SeedChunks(3, prefix: "zqx-secret-");

        Assert.Equal(1, WithSilentStderr(() => Embed()));
        Assert.Contains("words only: the provider's vectors have 1536 dimensions; the archive holds 384", _h.Err.ToString());
        Assert.Equal([EmbedProbe.Text], _h.Embedder.Texts);
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void FingerprintMismatch_Exit1_PrintsTheState()
    {
        EnableArchive();
        _h.SeedChunks(2);
        Assert.Equal(0, Embed());
        _h.SeedChunks(1, hostSessionId: "later", prefix: "later-");
        _h.Embedder = new FakeEmbedder { Descriptor = new EmbedderDescriptor("local", "other-model", "r", 384) };

        Assert.Equal(1, Embed());
        Assert.Contains(
            "words only: vectors were made by local/bge-small-en-v1.5; the running embedder is local/other-model",
            _h.Err.ToString());
        Assert.Empty(_h.Embedder.Texts);
        Assert.Equal(2, Embedded);
    }

    [Fact]
    public void ModelNotReady_Exit1_PrintsTheState()
    {
        EnableArchive();
        _h.SeedChunks(2);
        _h.Readiness = new EmbedderReadiness(false, "model.onnx is missing");

        Assert.Equal(1, Embed());
        Assert.Contains("words only: embedding model not ready (model.onnx is missing)", _h.Err.ToString());
        Assert.Empty(_h.Embedder.Texts);
    }

    [Fact]
    public void ThrowingFactory_Exit1_PrintsTheNotReadyState()
    {
        Remote(allow: true); // no endpoint: the real factory throws
        _h.SeedChunks(2);
        _h.Inspector = cfg => EmbedderInspection.Inspect(cfg);

        var code = WithSilentStderr(() => Embed());

        Assert.Equal(1, code);
        Assert.Contains(
            "words only: embedding model not ready (embedding.endpoint required when provider = 'openai')",
            _h.Err.ToString());
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void LeaseHeld_Exit1_SaysTryAgainLater()
    {
        EnableArchive();
        _h.SeedChunks(2);
        using (var store = _h.OpenArchive())
        {
            Assert.True(store.TryAcquireEmbedLease(
                new EmbedLeaseHolder("another-machine", 1, "run"), _h.Time.GetUtcNow(), (_, _) => true));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.Equal(1, Embed());
        Assert.Contains("another embed run is active; try again later", _h.Err.ToString());
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void EmbeddingFailed_Exit1_PrintsTheState()
    {
        EnableArchive();
        _h.SeedChunks(2);
        _h.Embedder.OnEmbed = _ => throw new IOException("the disk went away");

        var code = WithSilentStderr(() => Embed());

        Assert.Equal(1, code);
        Assert.Contains("embedding failed: the disk went away", _h.Err.ToString());
        Assert.Equal(0, Embedded);
    }

    [Fact]
    public void ChunksThatFailedAlone_AreCountedInTheClosingLine_ExitUnchanged()
    {
        EnableArchive();
        _h.SeedChunks(4);
        _h.Embedder.OnEmbed = text => { if (text == "chunk-1") throw new ArgumentException("this chunk is bad"); };

        var code = WithSilentStderr(() => Embed());

        Assert.Equal(0, code);
        Assert.Equal(3, Embedded);
        var output = _h.Out.ToString();
        Assert.Contains("done: embedded 3 chunks", output);
        Assert.Contains("1 could not be embedded", output);
    }

    [Fact]
    public void RetryFailed_ResetsTheCountsFirst_AndSaysHowMany()
    {
        EnableArchive();
        _h.SeedChunks(3);
        using (var conn = _h.Connect())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tx_chunk SET embed_attempts = 3 WHERE content IN ('chunk-0', 'chunk-2')";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(0, Embed());
        Assert.Equal(1, Embedded); // the two given up on stay out without the flag

        Assert.Equal(0, Embed("--retry-failed"));
        Assert.Contains("reset the failed tries of 2 chunks", _h.Out.ToString());
        Assert.Equal(3, Embedded);
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_chunk WHERE embed_attempts > 0"));
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new EmbedCommand();
        Assert.Equal("embed", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }
}
