using System;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>Why the embed worker may or may not run (spec section 3).</summary>
public enum EmbedState
{
    NotApplicable,
    RemoteNotAllowed,
    DimensionRefused,
    FingerprintMismatch,
    ModelNotReady,
    WaitingForReindex,
    EmbeddingFailed,
    AnotherRunActive,
    Ready,
}

/// <summary>
/// The facts the gate reads. <paramref name="Dimensions"/>, <paramref name="Fingerprint"/> and <paramref name="Current"/>
/// are null when the embedder factory threw: rules 1 and 2 are then evaluated from config, rules 3 and 4 are skipped
/// and rule 5 reports the factory message. <paramref name="Provider"/> is the effective provider (null or empty is local).
/// </summary>
public sealed record EmbedGateInput(
    bool ArchiveActive,
    bool Postgres,
    string Provider,
    bool AllowRemoteEmbedding,
    int? Dimensions,
    FingerprintState? Fingerprint,
    ArchiveFingerprint? Stored,
    ArchiveFingerprint? Current,
    EmbedderReadiness Readiness,
    bool MemoryReindexRunning,
    bool ServerPass);

/// <summary>A gate state with the text <c>status</c> and the CLI print.</summary>
public sealed record EmbedGateResult(EmbedState State, string Text)
{
    public bool CanEmbed => State == EmbedState.Ready;

    public bool MeaningUsable => State is EmbedState.Ready or EmbedState.WaitingForReindex;
}

public static class EmbedGate
{
    /// <summary>The fixed width of the archive's vector table.</summary>
    public const int ArchiveDimensions = 384;

    /// <summary>First failing rule wins, in the order of the spec's table.</summary>
    public static EmbedGateResult Evaluate(EmbedGateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.ArchiveActive || input.Postgres)
        {
            return new EmbedGateResult(EmbedState.NotApplicable, "not applicable");
        }

        if (!IsLocal(input.Provider) && !input.AllowRemoteEmbedding)
        {
            return new EmbedGateResult(
                EmbedState.RemoteNotAllowed,
                "words only: the embedding provider is remote and allow_remote_embedding is off");
        }

        if (input.Dimensions is int dims && dims != ArchiveDimensions)
        {
            return Dimension(dims);
        }

        if (input.Fingerprint == FingerprintState.Mismatch)
        {
            return Mismatch(input.Stored, input.Current);
        }

        if (!input.Readiness.Ready)
        {
            return NotReady(input.Readiness.Reason ?? "unknown");
        }

        if (input.ServerPass && input.MemoryReindexRunning)
        {
            return new EmbedGateResult(EmbedState.WaitingForReindex, "waiting: the memory re-embed is running");
        }

        return new EmbedGateResult(EmbedState.Ready, "ready");
    }

    /// <summary>The run-time not-ready result (the model could not be found when it was loaded).</summary>
    public static EmbedGateResult NotReady(string reason) =>
        new(EmbedState.ModelNotReady, $"words only: embedding model not ready ({reason})");

    /// <summary>A run that ended on an embedder failure.</summary>
    public static EmbedGateResult Failed(string message) =>
        new(EmbedState.EmbeddingFailed, $"embedding failed: {message}");

    /// <summary>The dimension refusal, from config (rule 3) or from the probe.</summary>
    public static EmbedGateResult Dimension(int actual) =>
        new(
            EmbedState.DimensionRefused,
            $"words only: the provider's vectors have {actual} dimensions; the archive holds {ArchiveDimensions}");

    /// <summary>Vectors of another model exist (rule 4, or found by the worker's write).</summary>
    public static EmbedGateResult Mismatch(ArchiveFingerprint? stored, ArchiveFingerprint? current) =>
        new(
            EmbedState.FingerprintMismatch,
            $"words only: vectors were made by {Render(stored)}; the running embedder is {Render(current)}");

    /// <summary>The embed lease is held by another run.</summary>
    public static EmbedGateResult AnotherRun() =>
        new(EmbedState.AnotherRunActive, "another embed run is active");

    private static bool IsLocal(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || string.Equals(provider.Trim(), "local", StringComparison.OrdinalIgnoreCase);

    private static string Render(ArchiveFingerprint? f) => f is null ? "unknown" : $"{f.Provider}/{f.Model}";
}
