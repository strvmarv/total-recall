using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts unforget (--session &lt;host&gt;:&lt;id&gt; | --project &lt;key&gt; | --cutoff)</c> (spec
/// section 8): needs a terminal; asks; removes the exclusion record or the cutoff and says whether there was one. It
/// brings nothing back: what was forgotten stays deleted, and only later archiving is allowed again.
/// </summary>
public sealed class UnforgetCommand : TranscriptsCommandBase
{
    public UnforgetCommand() { }

    public UnforgetCommand(
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

    public override string Name => "unforget";
    public override string Description =>
        "Allow forgotten transcripts to be archived again: unforget --session <host>:<id> | --project <key> | --cutoff";
    protected override string Usage =>
        "Usage: total-recall transcripts unforget (--session <host>:<id> | --project <key> | --cutoff)";

    protected override int Execute(string[] args)
    {
        var selector = ParseSelector(args, "--cutoff");
        if (selector is null)
            return 2;

        if (RequireTerminal() is { } noTerminal)
            return noTerminal;

        var location = ResolveLocation(ReadSettings());
        if (location is null)
            return 1;

        var store = OpenExisting(location, "there is nothing to unforget", out var openExit);
        if (store is null)
            return openExit;

        using (store)
        {
            var what = selector.Kind switch
            {
                SelectorKind.Session => $"the exclusion record of session {selector.SessionText}",
                SelectorKind.Project => $"the exclusion record of project {selector.Key}",
                _ => "the cutoff",
            };
            try
            {
                if (!Confirm($"Remove {what}? Later archiving may store it again; nothing already deleted comes back. Continue?"))
                {
                    Out.WriteLine("aborted; nothing was changed");
                    return 0;
                }

                var removed = selector.Kind switch
                {
                    SelectorKind.Session => store.UnforgetSession(selector.Host, selector.Id),
                    SelectorKind.Project => store.UnforgetProject(selector.Key),
                    _ => store.ClearCutoff(),
                };

                if (removed)
                    Out.WriteLine($"removed {what}");
                else
                    Out.WriteLine(selector.Kind == SelectorKind.Everything
                        ? "there was no cutoff; nothing was changed"
                        : $"there was no exclusion record for {(selector.Kind == SelectorKind.Session ? "session " + selector.SessionText : "project " + selector.Key)}; nothing was changed");
                return 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fail($"failed: {ex.Message}");
            }
        }
    }
}
