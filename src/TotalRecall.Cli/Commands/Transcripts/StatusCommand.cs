using System;
using System.Globalization;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts status</c> (spec section 8): the setting, whether the archive is active (the sentinel,
/// and a difference between the two), the location, the counts, the forgotten sessions and projects with their
/// identifiers, the cutoff, and always the line <see cref="TranscriptsCommandBase.SearchNotAvailableLine"/>. Needs no
/// terminal and never creates anything: the store is opened only when the archive tables already exist. Exit 1 when
/// the archive cannot be located or read (and then it never says that nothing is stored).
/// </summary>
public sealed class StatusCommand : TranscriptsCommandBase
{
    public StatusCommand() { }

    public StatusCommand(
        Func<TranscriptsSettings> settings,
        string mainDbPath,
        Func<string> storageMode,
        TranscriptSentinel sentinel,
        Func<bool> isInteractive,
        Func<string, bool> confirm,
        TextWriter output,
        TextWriter error,
        string userConfigPath)
        : base(settings, mainDbPath, storageMode, sentinel, isInteractive, confirm, output, error, userConfigPath) { }

    public override string Name => "status";
    public override string Description => "Show whether the transcript archive is on, where it is and what it holds";
    protected override string Usage => "Usage: total-recall transcripts status";

    protected override int Execute(string[] args)
    {
        if (RejectArguments(args) is { } usage)
            return usage;

        var exit = 0;
        try
        {
            exit = Report();
        }
        finally
        {
            Out.WriteLine(SearchNotAvailableLine);
        }
        return exit;
    }

    private int Report()
    {
        var exit = 0;

        // An unreadable configuration makes the settings loader return defaults: say nothing about the setting or
        // the contents then, only why.
        string storageMode;
        try
        {
            storageMode = ReadStorageMode();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"could not read the configuration: {ex.Message}");
        }

        var settings = ReadSettings();
        Out.WriteLine($"enabled = {(settings.Enabled ? "true" : "false")}");

        var sentinel = Sentinel;
        if (RequireAbsolute(sentinel.Path))
        {
            var active = sentinel.IsEnabled();
            Out.WriteLine($"active: {(active ? "yes" : "no")} (sentinel {sentinel.Path} {(active ? "present" : "absent")})");
            if (string.Equals(storageMode, "postgres", StringComparison.OrdinalIgnoreCase))
                Out.WriteLine("storage: postgres, where the transcript archive is not supported");
            if (TranscriptSentinel.IsActive(settings.Enabled, storageMode) != active)
            {
                Out.WriteLine("note: the setting and the sentinel differ; this applies at the next engine start");
            }
        }
        else
        {
            exit = 1;
        }

        var location = ResolveLocation(settings);
        if (location is null)
        {
            Out.WriteLine("location: unavailable");
            return 1;
        }
        Out.WriteLine($"location: {Describe(location)}");

        var opened = TranscriptStore.Open(location, create: false);
        switch (opened.Status)
        {
            case TranscriptOpenStatus.Absent:
                Out.WriteLine($"{(settings.Enabled ? "on" : "off")}, nothing stored");
                return exit;
            case TranscriptOpenStatus.Opened when opened.Store is not null:
                break;
            default:
                opened.Store?.Dispose();
                return Fail($"could not read the archive: {opened.Message ?? opened.Status.ToString()}");
        }

        TranscriptStatus status;
        using (var store = opened.Store)
        {
            try
            {
                status = store.Status();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fail($"could not read the archive: {ex.Message}");
            }
        }

        Print(status);
        return exit;
    }

    private void Print(TranscriptStatus s)
    {
        s.SessionsByKind.TryGetValue("interactive", out var interactive);
        s.SessionsByKind.TryGetValue("automated", out var automated);

        Out.WriteLine($"schema version: {s.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        Out.WriteLine($"sessions: {N(s.Sessions)} (interactive {N(interactive)}, automated {N(automated)})");
        Out.WriteLine($"chunks: {N(s.Chunks)}");

        if (s.ForgottenSessions.Count == 0)
        {
            Out.WriteLine("forgotten sessions: none");
        }
        else
        {
            Out.WriteLine($"forgotten sessions: {N(s.ForgottenSessions.Count)}");
            foreach (var f in s.ForgottenSessions)
                Out.WriteLine($"  {f.Host}:{f.HostSessionId} (at {f.At})");
        }

        if (s.ForgottenProjects.Count == 0)
        {
            Out.WriteLine("forgotten projects: none");
        }
        else
        {
            Out.WriteLine($"forgotten projects: {N(s.ForgottenProjects.Count)}");
            foreach (var p in s.ForgottenProjects)
            {
                var roots = p.Roots.Count == 0 ? "" : $", directories: {string.Join(", ", p.Roots)}";
                Out.WriteLine($"  {p.ProjectKey} (at {p.At}{roots})");
            }
        }

        Out.WriteLine($"cutoff: {s.IngestNotBefore ?? "none"}");
    }

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
}
