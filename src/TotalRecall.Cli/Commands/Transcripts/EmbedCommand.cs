using System;
using System.IO;
using System.Threading;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using EmbeddingConfig = TotalRecall.Core.Config.EmbeddingConfig;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts embed [--retry-failed]</c> (search the archive, spec section 4): gives every waiting
/// chunk its vector, with no budget, until nothing is left or Ctrl+C (the first press stops after the current batch, a
/// second ends the process), printing <c>embedded N of M (K remaining)</c> about every 5 seconds. Gates like
/// <c>backfill</c> (the archive on, not postgres, opened without creating anything) and is not terminal-gated. Exit 0
/// when it finished, was cancelled or found nothing to do; 1 when the gate says the worker cannot run (the state text is
/// printed), when another run holds the lease, when the run ended on a failure, or when the archive cannot be used; 2
/// for a usage error. Chunks that failed alone are counted in the closing line and do not change the code.
/// <c>--retry-failed</c> first sets every chunk's failed tries back to 0.
/// </summary>
public sealed class EmbedCommand : TranscriptsCommandBase
{
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly CancellationToken? _cancellation;

    /// <summary>Production: the configured embedder, cancelled by Ctrl+C.</summary>
    public EmbedCommand() { }

    /// <summary>Test seam. A null <paramref name="cancellation"/> listens to Ctrl+C as production does.</summary>
    public EmbedCommand(
        Func<TranscriptsSettings> settings,
        string mainDbPath,
        Func<string> storageMode,
        TranscriptSentinel sentinel,
        Func<bool> isInteractive,
        Func<string, bool> confirm,
        TextWriter output,
        TextWriter error,
        string userConfigPath,
        Func<EmbeddingConfig>? embeddingConfig,
        Func<EmbeddingConfig, EmbedderInspectionResult>? inspector,
        TimeProvider? time,
        CancellationToken? cancellation)
        : base(settings, mainDbPath, storageMode, sentinel, isInteractive, confirm, output, error, userConfigPath,
            embeddingConfig, inspector)
    {
        _time = time ?? TimeProvider.System;
        _cancellation = cancellation;
    }

    public override string Name => "embed";
    public override string Description => "Give the transcript archive's waiting chunks their vectors";
    protected override string Usage => "Usage: total-recall transcripts embed [--retry-failed]";

    protected override int Execute(string[] args)
    {
        var retryFailed = false;
        foreach (var a in args)
        {
            if (a == "--retry-failed")
                retryFailed = true;
            else if (a.StartsWith('-'))
                return UsageError($"unknown option '{a}'");
            else
                return UsageError("unexpected argument");
        }

        if (!ConfigReadable())
            return 1;

        var settings = ReadSettings();
        if (!settings.Enabled)
        {
            return Fail("the transcript archive is off; run 'total-recall transcripts enable' in a terminal to turn it on");
        }
        if (string.Equals(ReadStorageMode(), "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("the transcript archive is not supported with postgres storage " +
                        "(storage.mode or storage.connection_string selects it); it needs the local SQLite database");
        }

        var location = ResolveLocation(settings);
        if (location is null)
            return 1;

        var opened = TranscriptStore.Open(location, create: false);
        switch (opened.Status)
        {
            case TranscriptOpenStatus.Opened when opened.Store is not null:
                break;
            case TranscriptOpenStatus.Absent:
                return Fail($"the archive tables do not exist at {Describe(location)}; " +
                            "'total-recall transcripts enable' creates them");
            default:
                opened.Store?.Dispose();
                return Fail(CouldNotRead(location, opened));
        }

        using var store = opened.Store;
        try
        {
            return Embed(store, settings, retryFailed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] transcripts embed", ex);
            return Fail($"embedding failed: {ex.Message}");
        }
    }

    private int Embed(ITranscriptStore store, TranscriptsSettings settings, bool retryFailed)
    {
        if (retryFailed)
        {
            var reset = store.ResetEmbedAttempts();
            Out.WriteLine($"reset the failed tries of {Plural(reset, "chunk")}");
        }

        var counts = store.CountEmbedding();
        if (counts.Waiting == 0)
        {
            Out.WriteLine(counts.GaveUp > 0
                ? $"nothing to embed; {N(counts.GaveUp)} could not be embedded ('--retry-failed' tries them again)"
                : "nothing to embed");
            return 0;
        }

        using var check = CheckEmbedGate(store, settings, archiveActive: true, postgres: false);
        if (!check.Gate.CanEmbed)
            return Fail(check.Gate.Text);

        var run = WithCancelKey(_cancellation, ct => RunWorker(store, check, counts.Waiting, _time, ct, ""));
        var couldNot = CouldNotEmbed(run);
        var failedPart = couldNot > 0 ? $"; {N(couldNot)} could not be embedded" : "";

        switch (run.StoppedBy)
        {
            case EmbedStopReason.Done:
                Out.WriteLine($"done: embedded {N(run.Embedded)} chunks{failedPart}");
                return 0;
            case EmbedStopReason.Cancelled:
            case EmbedStopReason.Budget:
                Out.WriteLine($"interrupted: embedded {N(run.Embedded)} chunks, {N(run.Remaining)} remaining{failedPart}");
                return 0;
            case EmbedStopReason.Lease when run.State.State == EmbedState.AnotherRunActive:
                PrintPartial(run, failedPart);
                return Fail("another embed run is active; try again later");
            default:
                PrintPartial(run, failedPart);
                return Fail(run.State.Text);
        }
    }

    /// <summary>What a run that ended early did, when it did anything.</summary>
    private void PrintPartial(EmbedRun run, string failedPart)
    {
        if (run.Embedded > 0 || failedPart.Length > 0)
            Out.WriteLine($"embedded {N(run.Embedded)} chunks, {N(run.Remaining)} remaining{failedPart}");
    }
}
