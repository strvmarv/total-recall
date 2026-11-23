using System;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class EmbedGateTests
{
    private static readonly ArchiveFingerprint Bge = new("local", "bge-small-en-v1.5", "rev1", 384);
    private static readonly ArchiveFingerprint Other = new("openai", "text-embedding-3-small", "", 384);

    private static EmbedGateInput Ok() => new(
        ArchiveActive: true,
        Postgres: false,
        Provider: "local",
        AllowRemoteEmbedding: false,
        Dimensions: 384,
        Fingerprint: FingerprintState.Match,
        Stored: Bge,
        Current: Bge,
        Readiness: new EmbedderReadiness(true, null),
        MemoryReindexRunning: false,
        ServerPass: false);

    [Fact]
    public void Ready_WhenNothingFails()
    {
        var r = EmbedGate.Evaluate(Ok());
        Assert.Equal(EmbedState.Ready, r.State);
        Assert.Equal("ready", r.Text);
        Assert.True(r.CanEmbed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Rule1_OffOrPostgres_NotApplicable(bool active, bool postgres)
    {
        // Later rules would also fail, to prove rule 1 wins.
        var input = Ok() with
        {
            ArchiveActive = active, Postgres = postgres, Provider = "openai", Fingerprint = FingerprintState.Mismatch,
        };
        var r = EmbedGate.Evaluate(input);
        Assert.Equal(EmbedState.NotApplicable, r.State);
        Assert.Equal("not applicable", r.Text);
        Assert.False(r.CanEmbed);
        Assert.False(r.MeaningUsable);
    }

    [Fact]
    public void Rule2_RemoteWithoutOptIn()
    {
        var r = EmbedGate.Evaluate(Ok() with { Provider = "openai" });
        Assert.Equal(EmbedState.RemoteNotAllowed, r.State);
        Assert.Equal("words only: the embedding provider is remote and allow_remote_embedding is off", r.Text);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("bedrock")]
    [InlineData("BEDROCK")]
    public void Rule2_ProviderCasing_IsRemote(string provider)
    {
        var r = EmbedGate.Evaluate(Ok() with { Provider = provider });
        Assert.Equal(EmbedState.RemoteNotAllowed, r.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local")]
    [InlineData("LOCAL")]
    public void Provider_NullEmptyOrLocal_IsLocal(string? provider)
    {
        var r = EmbedGate.Evaluate(Ok() with { Provider = provider! });
        Assert.Equal(EmbedState.Ready, r.State);
    }

    [Fact]
    public void Rule3_OptedIn_768Dimensions_Refused()
    {
        var r = EmbedGate.Evaluate(Ok() with { Provider = "openai", AllowRemoteEmbedding = true, Dimensions = 768 });
        Assert.Equal(EmbedState.DimensionRefused, r.State);
        Assert.Equal("words only: the provider's vectors have 768 dimensions; the archive holds 384", r.Text);
    }

    [Fact]
    public void Rule4_Mismatch_NamesBothEmbedders()
    {
        var r = EmbedGate.Evaluate(Ok() with { Fingerprint = FingerprintState.Mismatch, Stored = Other, Current = Bge });
        Assert.Equal(EmbedState.FingerprintMismatch, r.State);
        Assert.Equal(
            "words only: vectors were made by openai/text-embedding-3-small; the running embedder is local/bge-small-en-v1.5",
            r.Text);
    }

    [Fact]
    public void Rule5_NotReady()
    {
        var r = EmbedGate.Evaluate(Ok() with { Readiness = new EmbedderReadiness(false, "model.onnx is missing") });
        Assert.Equal(EmbedState.ModelNotReady, r.State);
        Assert.Equal("words only: embedding model not ready (model.onnx is missing)", r.Text);
        Assert.False(r.MeaningUsable);
    }

    [Fact]
    public void Rule6_ServerPass_WithReindexRunning_Waits()
    {
        var input = Ok() with { MemoryReindexRunning = true, ServerPass = true };
        var r = EmbedGate.Evaluate(input);
        Assert.Equal(EmbedState.WaitingForReindex, r.State);
        Assert.Equal("waiting: the memory re-embed is running", r.Text);
        Assert.False(r.CanEmbed);
        Assert.True(r.MeaningUsable);

        var cli = EmbedGate.Evaluate(input with { ServerPass = false });
        Assert.Equal(EmbedState.Ready, cli.State);
    }

    [Fact]
    public void Precedence_RemoteBeatsMismatch()
    {
        var r = EmbedGate.Evaluate(Ok() with { Provider = "openai", Fingerprint = FingerprintState.Mismatch, Stored = Other });
        Assert.Equal(EmbedState.RemoteNotAllowed, r.State);
    }

    [Fact]
    public void Precedence_DimensionBeatsMismatch()
    {
        var r = EmbedGate.Evaluate(Ok() with
        {
            Provider = "openai", AllowRemoteEmbedding = true, Dimensions = 1536,
            Fingerprint = FingerprintState.Mismatch, Stored = Other,
        });
        Assert.Equal(EmbedState.DimensionRefused, r.State);
    }

    [Fact]
    public void Precedence_MismatchBeatsNotReady()
    {
        var r = EmbedGate.Evaluate(Ok() with
        {
            Fingerprint = FingerprintState.Mismatch, Stored = Other,
            Readiness = new EmbedderReadiness(false, "x"),
        });
        Assert.Equal(EmbedState.FingerprintMismatch, r.State);
    }

    [Fact]
    public void Precedence_NotReadyBeatsWaiting()
    {
        var r = EmbedGate.Evaluate(Ok() with
        {
            Readiness = new EmbedderReadiness(false, "x"), MemoryReindexRunning = true, ServerPass = true,
        });
        Assert.Equal(EmbedState.ModelNotReady, r.State);
    }

    [Fact]
    public void FactoryThrew_OptedIn_NullCurrent_ReportsFactoryMessageAsNotReady()
    {
        var r = EmbedGate.Evaluate(Ok() with
        {
            Provider = "openai", AllowRemoteEmbedding = true,
            Dimensions = null, Fingerprint = null, Current = null,
            Readiness = new EmbedderReadiness(false, "embedding.endpoint required when provider = 'openai'"),
        });
        Assert.Equal(EmbedState.ModelNotReady, r.State);
        Assert.Equal(
            "words only: embedding model not ready (embedding.endpoint required when provider = 'openai')", r.Text);
    }

    [Fact]
    public void FactoryThrew_NoOptIn_RemoteNotAllowedWins()
    {
        var r = EmbedGate.Evaluate(Ok() with
        {
            Provider = "openai", AllowRemoteEmbedding = false,
            Dimensions = null, Fingerprint = null, Current = null,
            Readiness = new EmbedderReadiness(false, "embedding.endpoint required when provider = 'openai'"),
        });
        Assert.Equal(EmbedState.RemoteNotAllowed, r.State);
    }

    [Fact]
    public void RunTimeHelpers()
    {
        var f = EmbedGate.Failed("boom");
        Assert.Equal(EmbedState.EmbeddingFailed, f.State);
        Assert.Equal("embedding failed: boom", f.Text);

        var d = EmbedGate.Dimension(768);
        Assert.Equal(EmbedState.DimensionRefused, d.State);
        Assert.Equal("words only: the provider's vectors have 768 dimensions; the archive holds 384", d.Text);

        var a = EmbedGate.AnotherRun();
        Assert.Equal(EmbedState.AnotherRunActive, a.State);
        Assert.Equal("another embed run is active", a.Text);

        var n = EmbedGate.NotReady("gone");
        Assert.Equal(EmbedState.ModelNotReady, n.State);
        Assert.Equal("words only: embedding model not ready (gone)", n.Text);
    }

    [Theory]
    [InlineData(EmbedState.Ready, true, true)]
    [InlineData(EmbedState.WaitingForReindex, false, true)]
    [InlineData(EmbedState.NotApplicable, false, false)]
    [InlineData(EmbedState.RemoteNotAllowed, false, false)]
    [InlineData(EmbedState.DimensionRefused, false, false)]
    [InlineData(EmbedState.FingerprintMismatch, false, false)]
    [InlineData(EmbedState.ModelNotReady, false, false)]
    [InlineData(EmbedState.EmbeddingFailed, false, false)]
    [InlineData(EmbedState.AnotherRunActive, false, false)]
    public void CanEmbed_MeaningUsable_TruthTable(EmbedState state, bool canEmbed, bool meaning)
    {
        var r = new EmbedGateResult(state, "t");
        Assert.Equal(canEmbed, r.CanEmbed);
        Assert.Equal(meaning, r.MeaningUsable);
    }

    [Fact]
    public void Probe_Text()
    {
        Assert.Equal("total-recall embedding probe", EmbedProbe.Text);
    }

    // ---- EmbedderInspection ----

    private static Core.Config.EmbeddingConfig Cfg(string? provider, string? endpoint = null) => new(
        "bge-small-en-v1.5", 384,
        provider is null ? FSharpOption<string>.None : FSharpOption<string>.Some(provider),
        endpoint is null ? FSharpOption<string>.None : FSharpOption<string>.Some(endpoint),
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None);

    [Fact]
    public void Inspection_ThrowingFactory_NullEmbedder_FactoryError()
    {
        var res = EmbedderInspection.Inspect(Cfg("openai"));
        Assert.Null(res.Embedder);
        Assert.Null(res.Current);
        Assert.False(res.Readiness.Ready);
        Assert.NotNull(res.FactoryError);
        Assert.Contains("endpoint", res.FactoryError);
        Assert.Equal(res.FactoryError, res.Readiness.Reason);
    }

    private sealed class FakeEmbedder : IEmbedder
    {
        public EmbedderDescriptor Descriptor { get; } = new("local", "fake", "r", 384);
        public float[] Embed(string text) => new float[384];
    }

    private sealed class NotReadyEmbedder : IEmbedder, IEmbedderReadiness
    {
        public EmbedderDescriptor Descriptor { get; } = new("local", "fake", "r", 384);
        public float[] Embed(string text) => new float[384];
        public EmbedderReadiness CheckReady() => new(false, "nope");
    }

    [Fact]
    public void Inspection_Factory_BuildsCurrent_AndTreatsPlainEmbedderAsReady()
    {
        int? seenThreads = -1;
        var res = EmbedderInspection.Inspect(Cfg(null), 2, (c, t) => { seenThreads = t; return new FakeEmbedder(); });
        Assert.Equal(2, seenThreads);
        Assert.NotNull(res.Embedder);
        Assert.Null(res.FactoryError);
        Assert.True(res.Readiness.Ready);
        Assert.Equal(new ArchiveFingerprint("local", "fake", "r", 384), res.Current);
    }

    [Fact]
    public void Inspection_UsesEmbedderReadiness()
    {
        var res = EmbedderInspection.Inspect(Cfg(null), null, (c, t) => new NotReadyEmbedder());
        Assert.False(res.Readiness.Ready);
        Assert.Equal("nope", res.Readiness.Reason);
        Assert.NotNull(res.Current);
    }

    [Fact]
    public void Inspection_RealLocalConfig_ConstructsWithoutLoadingModel()
    {
        var res = EmbedderInspection.Inspect(Cfg("local"));
        try
        {
            Assert.IsType<OnnxEmbedder>(res.Embedder);
            Assert.False(((OnnxEmbedder)res.Embedder!).IsLoaded);
            Assert.Equal(384, res.Current!.Dimensions);
        }
        finally { (res.Embedder as IDisposable)?.Dispose(); }
    }
}
