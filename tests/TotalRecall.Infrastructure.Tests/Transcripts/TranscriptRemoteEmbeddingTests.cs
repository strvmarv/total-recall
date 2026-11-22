using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The remote-provider rule, the fingerprint and the model readiness through both paths that could send archive text to
/// an embedder: the <c>session_start</c> pass (<see cref="ArchivePass"/>) and <c>transcript_search</c>
/// (<see cref="TranscriptSearchService"/>), over one file-backed archive (search the archive, spec section 8, tests 8
/// to 10). Recording fakes, no model. In the process-environment collection because the runs that refuse log through
/// <see cref="Console.Error"/>, which these tests silence.
/// </summary>
[Collection("process-environment")]
public sealed class TranscriptRemoteEmbeddingTests : IDisposable
{
    private const string Marker = "zqxremote";
    private const string RemoteOff = "words only: the embedding provider is remote and allow_remote_embedding is off";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly TranscriptTestDb _db = new();
    private readonly List<ArchivePass> _passes = new();
    private readonly TextWriter _stderr = Console.Error;

    public TranscriptRemoteEmbeddingTests() => Console.SetError(new StringWriter());

    public void Dispose()
    {
        foreach (var p in _passes)
        {
            p.Dispose();
            try { p.WaitIdleAsync().Wait(Wait); } catch { /* best effort */ }
        }
        Console.SetError(_stderr);
        _db.Dispose();
    }

    // ---- fakes and helpers ---------------------------------------------------------------------------------------

    /// <summary>A local embedder whose readiness check reports the model missing; records any call.</summary>
    private sealed class NotReadyEmbedder : IEmbedder, IEmbedderReadiness
    {
        public EmbedderDescriptor Descriptor { get; } = new("local", "bge-small-en-v1.5", "rev1", 384);
        public List<string> Calls { get; } = new();
        public EmbedderReadiness CheckReady() => new(false, "model.onnx is missing");

        public float[] Embed(string text)
        {
            Calls.Add(text);
            throw new InvalidOperationException("the model is not there");
        }
    }

    private static Core.Config.EmbeddingConfig Embedding(string provider) => new(
        "bge-small-en-v1.5", 384,
        provider == "local" ? FSharpOption<string>.None : FSharpOption<string>.Some(provider),
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None);

    private static TranscriptsSettings Settings(bool allowRemote) => new(true, string.Empty, null, 0, allowRemote);

    private TranscriptLocation Location => new(_db.Path, IsMainDatabase: false);

    private static EmbedderInspectionResult Inspected(IEmbedder e)
    {
        var d = e.Descriptor;
        return new EmbedderInspectionResult(
            e, new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions),
            (e as IEmbedderReadiness)?.CheckReady() ?? new EmbedderReadiness(true, null), null);
    }

    /// <summary>One pass over the archive with no adapters (the chunks are seeded), the embedder handed out by the inspection.</summary>
    private async Task<(ArchivePassResult Result, int Inspections)> RunPass(IEmbedder embedder, string provider, bool allowRemote)
    {
        var inspections = 0;
        var pass = new ArchivePass(
            () => new ArchivePassContext(
                Settings(allowRemote), Location, "sqlite", Embedding(provider),
                (_, _) => { Interlocked.Increment(ref inspections); return Inspected(embedder); },
                () => false,
                () => Array.Empty<ITranscriptAdapter>()),
            () => true,
            new ArchivePassOptions(EmbedBudget: new EmbedBudget(null, null), MinInterval: TimeSpan.Zero));
        _passes.Add(pass);

        pass.Kick();
        await pass.WaitIdleAsync().WaitAsync(Wait);
        Assert.NotNull(pass.Last);
        Assert.Equal(ArchivePass.OutcomeDone, pass.Last!.Outcome);
        Assert.NotNull(pass.Last.Embed);
        return (pass.Last, inspections);
    }

    private TranscriptSearchResponse Search(IEmbedder embedder, string provider, bool allowRemote, string query = "staging deploy " + Marker) =>
        new TranscriptSearchService(
                () => new TranscriptSearchContext(Location, Settings(allowRemote), "sqlite", Embedding(provider), embedder),
                new ManualTimeProvider(Now))
            .Search(new TranscriptSearchRequest(query, null, null, null, null, false, 10), CancellationToken.None);

    /// <summary>
    /// Three interactive sessions of one chunk each (so the two-per-session cap of a search keeps them all), every
    /// chunk holding <see cref="Marker"/>; returns their contents in id order.
    /// </summary>
    private List<string> Seed(string prefix = "remote", string topic = "staging deploy")
    {
        var contents = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var s = _db.AddSession("claude-code", $"{prefix}-{i}");
            var content = $"proj | 2026-03-0{i + 1} | interactive\n\nwe talked about the {topic} {Marker} number {i} of {prefix}";
            _db.AddChunk(s, content);
            contents.Add(content);
        }
        return contents;
    }

    private long Embedded => _db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1");
    private long Vectors => _db.Scalar("SELECT count(*) FROM tx_chunk_vec");

    private static void AssertNoArchiveText(IEnumerable<string> calls) =>
        Assert.DoesNotContain(calls, c => c.Contains(Marker, StringComparison.Ordinal) && c.Contains("we talked about", StringComparison.Ordinal));

    private static void AssertFoundByWords(TranscriptSearchResponse r, int expected = 3)
    {
        Assert.Null(r.Error);
        Assert.Equal(expected, r.Results.Count);
        Assert.All(r.Results, h => Assert.DoesNotContain("meaning", h.Via));
        Assert.All(r.Results, h => Assert.Contains(Marker, h.Text));
    }

    // ---- the remote rule -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Remote_NoOptIn_PassAndSearch_RecordedNoArchiveTextAndNoQuery()
    {
        Seed();
        var remote = new RecordingEmbedder("openai", 384);

        var (result, inspections) = await RunPass(remote, "openai", allowRemote: false);
        var search = Search(remote, "openai", allowRemote: false);

        Assert.Empty(remote.Calls);   // neither a chunk nor the probe nor the query
        Assert.Equal(0, inspections); // the embedder was not even built: rules 1 and 2 come from config
        Assert.Equal(EmbedState.RemoteNotAllowed, result.Embed!.State.State);
        Assert.Equal(RemoteOff, result.Embed.State.Text);
        Assert.Equal(EmbedStopReason.Gate, result.Embed.StoppedBy);
        Assert.Equal(3, result.Embed.Remaining);
        Assert.Equal(0, Embedded);
        Assert.Null(_db.Store.ReadEmbedRun()); // a gate refusal leaves no run record

        Assert.False(search.MeaningOn);
        Assert.Equal(RemoteOff, search.MeaningReason);
        Assert.Equal(3, search.PendingEmbedding);
        AssertFoundByWords(search);
    }

    [Fact]
    public async Task Remote_OptIn_384_ReceivesChunkText_StampsOpenAi_SearchUsesVectors()
    {
        var contents = Seed();
        var remote = new RecordingEmbedder("openai", 384);

        var (result, _) = await RunPass(remote, "openai", allowRemote: true);

        Assert.Equal(EmbedStopReason.Done, result.Embed!.StoppedBy);
        Assert.Equal(3, result.Embed.Embedded);
        Assert.Equal(3, Embedded);
        Assert.Equal(3, Vectors);
        Assert.Equal(EmbedProbe.Text, remote.Calls[0]); // the probe goes first
        Assert.Equal(contents, remote.Calls.Skip(1).ToList()); // then every chunk, once, in id order
        var stamped = _db.Store.ReadEmbedFingerprint();
        Assert.Equal(new ArchiveFingerprint("openai", "recording-model", "1", 384), stamped);

        var before = remote.Calls.Count;
        var search = Search(remote, "openai", allowRemote: true);

        Assert.True(search.MeaningOn);
        Assert.Null(search.MeaningReason);
        Assert.Equal(0, search.PendingEmbedding);
        Assert.Equal(new[] { "staging deploy " + Marker }, remote.Calls.Skip(before).ToArray()); // the query, opted in
        Assert.Equal(3, search.Results.Count);
        Assert.All(search.Results, h => Assert.Contains("meaning", h.Via));
    }

    [Fact]
    public async Task Remote_OptIn_1536Descriptor_ReceivesNothing_DimensionRefusalState()
    {
        Seed();
        var remote = new RecordingEmbedder("openai", 1536);
        const string refusal = "words only: the provider's vectors have 1536 dimensions; the archive holds 384";

        var (result, _) = await RunPass(remote, "openai", allowRemote: true);
        var search = Search(remote, "openai", allowRemote: true);

        Assert.Empty(remote.Calls); // rule 3 refuses before any call: not even the probe or the query
        Assert.Equal(EmbedState.DimensionRefused, result.Embed!.State.State);
        Assert.Equal(refusal, result.Embed.State.Text);
        Assert.Equal(EmbedStopReason.Gate, result.Embed.StoppedBy);
        Assert.Equal(0, Embedded);
        Assert.Null(_db.Store.ReadEmbedFingerprint());

        Assert.False(search.MeaningOn);
        Assert.Equal(refusal, search.MeaningReason);
        AssertFoundByWords(search);
    }

    [Fact]
    public async Task Remote_OptIn_DescriptorSays384_Returns768_ReceivesOnlyTheProbe_DimensionRefusalState()
    {
        Seed();
        var remote = new RecordingEmbedder("openai", 384, 768);
        const string refusal = "words only: the provider's vectors have 768 dimensions; the archive holds 384";

        var (result, _) = await RunPass(remote, "openai", allowRemote: true);

        Assert.Equal(new[] { EmbedProbe.Text }, remote.Calls.ToArray()); // the probe, never chunk text
        Assert.Equal(EmbedState.DimensionRefused, result.Embed!.State.State);
        Assert.Equal(EmbedStopReason.Failed, result.Embed.StoppedBy);
        Assert.Equal(refusal, _db.Store.ReadEmbedRun()!.State);
        Assert.Equal(0, Embedded);
        Assert.Equal(0, Vectors);
        Assert.Null(_db.Store.ReadEmbedFingerprint());

        // The search sends the query (the opt-in allows it), finds the answer's length wrong and falls back to words.
        var search = Search(remote, "openai", allowRemote: true);
        Assert.Equal(new[] { EmbedProbe.Text, "staging deploy " + Marker }, remote.Calls.ToArray());
        AssertNoArchiveText(remote.Calls);
        Assert.False(search.MeaningOn);
        Assert.Equal(refusal, search.MeaningReason);
        AssertFoundByWords(search);
    }

    // ---- the fingerprint -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Fingerprint_Mismatch_WordsOnly_NothingEmbedded_NoMixing()
    {
        Seed("first", "staging deploy");
        var modelA = new ScriptedEmbedder(); // local bge-small-en-v1.5 rev1
        var (first, _) = await RunPass(modelA, "local", allowRemote: false);
        Assert.Equal(3, first.Embed!.Embedded);
        var stampedA = _db.Store.ReadEmbedFingerprint();
        Assert.Equal(new ArchiveFingerprint("local", "bge-small-en-v1.5", "rev1", 384), stampedA);
        var vectorsBefore = _db.Text("SELECT group_concat(rowid) FROM (SELECT rowid FROM tx_chunk_vec ORDER BY rowid)");

        Seed("later", "staging deploy");
        var modelB = new RecordingEmbedder("local", 384); // another local model: recording-model
        var (second, _) = await RunPass(modelB, "local", allowRemote: false);

        const string mismatch = "words only: vectors were made by local/bge-small-en-v1.5; the running embedder is local/recording-model";
        Assert.Empty(modelB.Calls);
        Assert.Equal(EmbedState.FingerprintMismatch, second.Embed!.State.State);
        Assert.Equal(mismatch, second.Embed.State.Text);
        Assert.Equal(EmbedStopReason.Gate, second.Embed.StoppedBy);
        Assert.Equal(3, Embedded); // only model A's
        Assert.Equal(3, Vectors);
        Assert.Equal(vectorsBefore, _db.Text("SELECT group_concat(rowid) FROM (SELECT rowid FROM tx_chunk_vec ORDER BY rowid)"));
        Assert.Equal(stampedA, _db.Store.ReadEmbedFingerprint());
        Assert.Equal(0, _db.Scalar(
            "SELECT count(*) FROM tx_chunk_vec v JOIN tx_chunk c ON c.id = v.rowid JOIN tx_session s ON s.id = c.session_id " +
            "WHERE s.host_session_id LIKE 'later-%'"));

        var search = Search(modelB, "local", allowRemote: false);
        Assert.Empty(modelB.Calls); // no query embedded with the other model
        Assert.False(search.MeaningOn);
        Assert.Equal(mismatch, search.MeaningReason);
        Assert.Equal(6, search.PendingEmbedding); // meaning is off, so every chunk in scope counts
        AssertFoundByWords(search, expected: 6);
    }

    [Fact]
    public async Task Fingerprint_AfterForgetEverything_NewModelRestamps_NotMismatch()
    {
        Seed("first", "staging deploy");
        var (first, _) = await RunPass(new ScriptedEmbedder(), "local", allowRemote: false);
        Assert.Equal(3, first.Embed!.Embedded);
        Assert.Equal("bge-small-en-v1.5", _db.Store.ReadEmbedFingerprint()!.Model);

        var forgot = _db.Store.ForgetAll(Now);
        Assert.True(forgot.WalTruncated, forgot.Warning);
        Assert.Equal(0, Vectors);

        var contents = Seed("after", "staging deploy");
        var modelB = new RecordingEmbedder("local", 384);
        Assert.Equal(FingerprintState.Unstamped, _db.Store.CheckFingerprint(new ArchiveFingerprint("local", "recording-model", "1", 384)));
        var (second, _) = await RunPass(modelB, "local", allowRemote: false);

        Assert.Equal(EmbedStopReason.Done, second.Embed!.StoppedBy);
        Assert.Equal(EmbedState.Ready, second.Embed.State.State);
        Assert.Equal(3, second.Embed.Embedded);
        Assert.Equal(contents, modelB.Calls.Skip(1).ToList());
        Assert.Equal(new ArchiveFingerprint("local", "recording-model", "1", 384), _db.Store.ReadEmbedFingerprint());

        var search = Search(modelB, "local", allowRemote: false);
        Assert.True(search.MeaningOn);
        Assert.All(search.Results, h => Assert.Contains("meaning", h.Via));
    }

    // ---- readiness -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ModelNotReady_PassEndsWithState_ChunksStay_WordsSearchWorks()
    {
        Seed();
        var notReady = new NotReadyEmbedder();
        const string state = "words only: embedding model not ready (model.onnx is missing)";

        var (result, inspections) = await RunPass(notReady, "local", allowRemote: false);

        Assert.Equal(1, inspections);
        Assert.Empty(notReady.Calls);
        Assert.Equal(EmbedState.ModelNotReady, result.Embed!.State.State);
        Assert.Equal(state, result.Embed.State.Text);
        Assert.Equal(EmbedStopReason.Gate, result.Embed.StoppedBy);
        Assert.Equal(3, result.Embed.Remaining);
        Assert.Equal(0, Embedded);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk WHERE embed_attempts > 0")); // nothing counted

        var search = Search(notReady, "local", allowRemote: false);
        Assert.Empty(notReady.Calls);
        Assert.False(search.MeaningOn);
        Assert.Equal(state, search.MeaningReason);
        Assert.Equal(3, search.PendingEmbedding);
        AssertFoundByWords(search);
    }
}
