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
/// <c>total-recall transcripts backfill [--host claude-code] [--no-embed]</c> (spec section 9; search the archive,
/// section 4): stores the host's existing sessions in the archive, then gives the waiting chunks their vectors, and
/// prints what it did. Needs no terminal and asks nothing. The order is part of the contract: arguments, the
/// configuration (strictly), the archive on and the storage mode not postgres, the archive's location, the archive
/// opened without creating anything (an older one is migrated here), and only then the adapter, so that while the
/// archive is off nothing under the host's directory is read. The embed step runs after the ingest, also when the host
/// has no session files, and is skipped when the ingest was interrupted or <c>--no-embed</c> is given (which prints only
/// the waiting count). Exit 0 when no file failed (also an interrupted run), 1 when a file failed or the archive cannot
/// be used, 2 for a usage error; the embed step never changes it (an embed that cannot run is reported as
/// <c>N chunks wait for embedding: &lt;state&gt;</c>). Ctrl+C stops after the current session or batch; a second
/// Ctrl+C ends the process.
/// </summary>
public sealed class BackfillCommand : TranscriptsCommandBase
{
    public const string ClaudeCode = "claude-code";
    private const int ProgressEvery = 50;

    private readonly Func<string, ITranscriptAdapter>? _adapterFactory;
    private readonly CancellationToken? _cancellation;
    private readonly TimeProvider _time = TimeProvider.System;

    /// <summary>Production: the Claude Code adapter, cancelled by Ctrl+C.</summary>
    public BackfillCommand() { }

    /// <summary>Test seam.</summary>
    public BackfillCommand(
        Func<TranscriptsSettings> settings,
        string mainDbPath,
        Func<string> storageMode,
        TranscriptSentinel sentinel,
        Func<bool> isInteractive,
        Func<string, bool> confirm,
        TextWriter output,
        TextWriter error,
        string userConfigPath,
        Func<string, ITranscriptAdapter> adapterFactory,
        CancellationToken cancellation,
        Func<EmbeddingConfig>? embeddingConfig = null,
        Func<EmbeddingConfig, EmbedderInspectionResult>? inspector = null,
        TimeProvider? time = null)
        : base(settings, mainDbPath, storageMode, sentinel, isInteractive, confirm, output, error, userConfigPath,
            embeddingConfig, inspector)
    {
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        _cancellation = cancellation;
        _time = time ?? TimeProvider.System;
    }

    public override string Name => "backfill";
    public override string Description => "Store the existing sessions of a host in the transcript archive";
    protected override string Usage => "Usage: total-recall transcripts backfill [--host claude-code] [--no-embed]";

    protected override int Execute(string[] args)
    {
        var host = ParseHost(args, out var noEmbed, out var usage);
        if (host is null)
            return usage;

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
        // Only now: nothing of the host's directory has been touched before this point.
        var adapter = CreateAdapter(host);

        return WithCancelKey(_cancellation, ct => IngestThenEmbed(store, adapter, settings, noEmbed, ct));
    }

    private int IngestThenEmbed(
        ITranscriptStore store, ITranscriptAdapter adapter, TranscriptsSettings settings, bool noEmbed, CancellationToken ct)
    {
        if (!adapter.Detect())
        {
            Out.WriteLine($"no Claude Code sessions found at {adapter.Root}");
            // The host's files may be gone while chunks still wait: that is the case the archive exists for.
            EmbedStep(store, settings, noEmbed, ct);
            return 0;
        }

        var summary = Run(store, adapter, ct);
        Print(adapter.Host, summary);
        if (!summary.Cancelled) // the person asked to stop
            EmbedStep(store, settings, noEmbed, ct);
        return summary.Failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The embed step: never changes the exit code and never throws. With <c>--no-embed</c> only the waiting count is
    /// printed; with nothing waiting nothing is printed.
    /// </summary>
    private void EmbedStep(ITranscriptStore store, TranscriptsSettings settings, bool noEmbed, CancellationToken ct)
    {
        long waiting;
        try
        {
            waiting = store.CountEmbedding().Waiting;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] transcripts backfill: counting the waiting chunks", ex);
            Out.WriteLine($"  the chunks waiting for embedding could not be counted: {ex.Message}");
            return;
        }

        if (noEmbed)
        {
            Out.WriteLine($"  {N(waiting)} chunks wait for embedding");
            return;
        }
        if (waiting == 0)
            return;

        try
        {
            using var check = CheckEmbedGate(store, settings, archiveActive: true, postgres: false);
            if (!check.Gate.CanEmbed)
            {
                Out.WriteLine($"  {N(waiting)} chunks wait for embedding: {check.Gate.Text}");
                return;
            }

            var run = RunWorker(store, check, waiting, _time, ct, "  ");
            switch (run.StoppedBy)
            {
                case EmbedStopReason.Done:
                case EmbedStopReason.Budget:
                case EmbedStopReason.Cancelled:
                    var line = $"  embedded {N(run.Embedded)} chunks";
                    var couldNot = CouldNotEmbed(run);
                    if (couldNot > 0)
                        line += $"; {N(couldNot)} could not be embedded";
                    if (run.StoppedBy != EmbedStopReason.Done && run.Remaining > 0)
                        line += $"; interrupted, {N(run.Remaining)} chunks wait for embedding";
                    Out.WriteLine(line);
                    break;
                default:
                    if (run.Embedded > 0)
                        Out.WriteLine($"  embedded {N(run.Embedded)} chunks");
                    Out.WriteLine($"  {N(run.Remaining)} chunks wait for embedding: {run.State.Text}");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] transcripts backfill: embedding", ex);
            Out.WriteLine($"  {N(waiting)} chunks wait for embedding: embedding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The Ctrl+C handler's decision, the value for <see cref="ConsoleCancelEventArgs.Cancel"/>. The first press cancels
    /// <paramref name="cts"/> and keeps the process, so the run stops after the current session and prints what it did;
    /// a second press (the run is already stopping) returns false and ends the process as usual.
    /// </summary>
    public static bool KeepProcessOnCancelKey(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        var keep = !cts.IsCancellationRequested;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return keep;
    }

    private BackfillSummary Run(ITranscriptStore store, ITranscriptAdapter adapter, CancellationToken ct) =>
        new TranscriptBackfill(store, adapter).Run(ct, p =>
        {
            if (p.Done % ProgressEvery == 0)
                Out.WriteLine($"  {N(p.Done)}/{N(p.Total)} sessions");
        });

    private ITranscriptAdapter CreateAdapter(string host) =>
        _adapterFactory is not null ? _adapterFactory(host) : new ClaudeCodeAdapter();

    /// <summary>
    /// The host: <c>--host</c>'s value or <c>claude-code</c>, and whether <c>--no-embed</c> was given; null after a
    /// usage error was printed.
    /// </summary>
    private string? ParseHost(string[] args, out bool noEmbed, out int usage)
    {
        usage = 0;
        noEmbed = false;
        string? host = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--no-embed")
            {
                noEmbed = true;
            }
            else if (a == "--host")
            {
                if (i + 1 >= args.Length || host is not null)
                {
                    usage = UsageError(host is not null ? "--host is given once" : "--host needs a value");
                    return null;
                }
                host = args[++i];
            }
            else if (a.StartsWith('-'))
            {
                usage = UsageError($"unknown option '{a}'");
                return null;
            }
            else
            {
                usage = UsageError("unexpected argument");
                return null;
            }
        }

        host ??= ClaudeCode;
        if (host != ClaudeCode)
        {
            usage = UsageError($"unknown host '{host}'; the only host is {ClaudeCode}");
            return null;
        }
        return host;
    }

    private void Print(string host, BackfillSummary s)
    {
        Out.WriteLine($"backfill {host}: {N(s.Seen)} sessions seen");
        Out.WriteLine($"  new {N(s.New)}, updated {N(s.Updated)}, unchanged {N(s.Unchanged)}, excluded {N(s.Excluded)}, " +
                      $"empty {N(s.Empty)}, skipped {N(s.Skipped)}, changed {N(s.Changed)}, retry {N(s.Retry)}, failed {N(s.Failed)}");
        Out.WriteLine($"  chunks: new {N(s.ChunksNew)}, changed {N(s.ChunksChanged)}, unchanged {N(s.ChunksUnchanged)}, removed {N(s.ChunksRemoved)}");
        if (s.SkippedLines > 0)
            Out.WriteLine($"  unreadable lines: {N(s.SkippedLines)}");
        foreach (var id in s.ChangedSessions)
            Out.WriteLine($"  changed (kept as stored): {id}");
        if (s.Cancelled)
            Out.WriteLine("  interrupted: the remaining sessions were not processed");
    }
}
