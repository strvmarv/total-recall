using System;
using System.Globalization;
using System.IO;
using System.Threading;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts backfill [--host claude-code]</c> (spec section 9): stores the host's existing sessions in
/// the archive and prints what it did. Needs no terminal and asks nothing. The order is part of the contract: arguments,
/// the configuration (strictly), the archive on and the storage mode not postgres, the archive's location, the archive
/// opened without creating anything (an older one is migrated here), and only then the adapter, so that while the
/// archive is off nothing under the host's directory is read. Exit 0 when no file failed (also an interrupted run), 1
/// when a file failed or the archive cannot be used, 2 for a usage error. Ctrl+C stops after the current session.
/// </summary>
public sealed class BackfillCommand : TranscriptsCommandBase
{
    public const string ClaudeCode = "claude-code";
    private const int ProgressEvery = 50;

    private readonly Func<string, ITranscriptAdapter>? _adapterFactory;
    private readonly CancellationToken? _cancellation;

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
        CancellationToken cancellation)
        : base(settings, mainDbPath, storageMode, sentinel, isInteractive, confirm, output, error, userConfigPath)
    {
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        _cancellation = cancellation;
    }

    public override string Name => "backfill";
    public override string Description => "Store the existing sessions of a host in the transcript archive";
    protected override string Usage => "Usage: total-recall transcripts backfill [--host claude-code]";

    protected override int Execute(string[] args)
    {
        var host = ParseHost(args, out var usage);
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
                return Fail($"could not read the archive at {Describe(location)}: {opened.Message ?? opened.Status.ToString()}");
        }

        using var store = opened.Store;
        // Only now: nothing of the host's directory has been touched before this point.
        var adapter = CreateAdapter(host);

        if (!adapter.Detect())
        {
            Out.WriteLine($"no Claude Code sessions found at {adapter.Root}");
            return 0;
        }

        BackfillSummary summary;
        if (_cancellation is { } injected)
        {
            summary = Run(store, adapter, injected);
        }
        else
        {
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) =>
            {
                e.Cancel = true; // stop after the current session instead of ending the process
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            };
            Console.CancelKeyPress += handler;
            try
            {
                summary = Run(store, adapter, cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        Print(adapter.Host, summary);
        return summary.Failed == 0 ? 0 : 1;
    }

    private BackfillSummary Run(ITranscriptStore store, ITranscriptAdapter adapter, CancellationToken ct) =>
        new TranscriptBackfill(store, adapter).Run(ct, p =>
        {
            if (p.Done % ProgressEvery == 0)
                Out.WriteLine($"  {N(p.Done)}/{N(p.Total)} sessions");
        });

    private ITranscriptAdapter CreateAdapter(string host) =>
        _adapterFactory is not null ? _adapterFactory(host) : new ClaudeCodeAdapter();

    /// <summary>The host: <c>--host</c>'s value or <c>claude-code</c>; null after a usage error was printed.</summary>
    private string? ParseHost(string[] args, out int usage)
    {
        usage = 0;
        string? host = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--host")
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

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
}
