using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts forget (--session &lt;host&gt;:&lt;id&gt; | --project &lt;key&gt; | --all)</c> (spec
/// section 8): exactly one selector; needs a terminal; prints how many sessions and chunks will go, asks, deletes, and
/// prints the counts. Works whether or not the archive is enabled; with no archive tables it creates nothing and says
/// nothing is stored. When the write-ahead log could not be truncated it prints the warning and exits 1: the rows are
/// deleted, but their text may still be in the log.
/// </summary>
public sealed class ForgetCommand : TranscriptsCommandBase
{
    public ForgetCommand() { }

    public ForgetCommand(
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

    public override string Name => "forget";
    public override string Description =>
        "Delete archived transcripts and keep them out: forget --session <host>:<id> | --project <key> | --all";
    protected override string Usage => "Usage: total-recall transcripts forget (--session <host>:<id> | --project <key> | --all)";

    protected override int Execute(string[] args)
    {
        var selector = ParseSelector(args, "--all");
        if (selector is null)
            return 2;

        if (RequireTerminal() is { } noTerminal)
            return noTerminal;

        var location = ResolveLocation(ReadSettings());
        if (location is null)
            return 1;

        var store = OpenExisting(location, "there is nothing to forget", out var openExit);
        if (store is null)
            return openExit;

        using (store)
        {
            try
            {
                var count = selector.Kind switch
                {
                    SelectorKind.Session => store.CountSession(selector.Host, selector.Id),
                    SelectorKind.Project => store.CountProject(selector.Key),
                    _ => store.CountAll(),
                };

                var target = selector.Kind switch
                {
                    SelectorKind.Session => $"session {selector.SessionText}",
                    SelectorKind.Project => $"project {selector.Key}",
                    _ => "everything",
                };
                var keepOut = selector.Kind switch
                {
                    SelectorKind.Session => "and records the session so it is not archived again",
                    SelectorKind.Project => "and records the project (and its directories) so it is not archived again",
                    _ => "and sets a cutoff so nothing from before now is archived again",
                };
                Out.WriteLine(
                    $"forgetting {target} deletes {Plural(count.Sessions, "session")} and {Plural(count.Chunks, "chunk")} " +
                    $"from {The(location)}, {keepOut}");
                Out.Flush();

                if (!Confirm("This cannot be undone. Continue?"))
                {
                    Out.WriteLine("aborted; nothing was changed");
                    return 0;
                }

                var result = selector.Kind switch
                {
                    SelectorKind.Session => store.ForgetSession(selector.Host, selector.Id),
                    SelectorKind.Project => store.ForgetProject(selector.Key),
                    _ => store.ForgetAll(DateTimeOffset.UtcNow),
                };

                var counts = $"{Plural(result.Sessions, "session")} and {Plural(result.Chunks, "chunk")}";
                if (!result.WalTruncated)
                {
                    return Fail($"deleted {counts}, but the write-ahead log could not be truncated: " +
                                (result.Warning ?? "run the command again"));
                }
                Out.WriteLine($"forgot {counts}");
                return 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fail($"failed: {ex.Message}");
            }
        }
    }
}
