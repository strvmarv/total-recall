using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts disable</c>: turns the archive off (spec section 8). Needs no terminal: it writes
/// <c>transcripts.enabled = false</c> and removes the sentinel, and keeps what the archive holds.
/// </summary>
public sealed class DisableCommand : TranscriptsCommandBase
{
    public DisableCommand() { }

    public DisableCommand(
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

    public override string Name => "disable";
    public override string Description => "Turn the transcript archive off (what it holds is kept)";
    protected override string Usage => "Usage: total-recall transcripts disable";

    protected override int Execute(string[] args)
    {
        if (RejectArguments(args) is { } usage)
            return usage;

        var sentinel = Sentinel;
        var configPath = UserConfigPath;
        if (!RequireAbsolute(configPath) || !RequireAbsolute(sentinel.Path))
            return 1;

        // Turning off is the safe direction: the sentinel is removed even when the setting cannot be written.
        string? writeError = null;
        try
        {
            ConfigWriter.SaveUserOverride(configPath, "transcripts.enabled", false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            writeError = ex.Message;
        }

        string? removeError = null;
        try
        {
            sentinel.Remove();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            removeError = ex.Message;
        }

        if (writeError is not null && removeError is not null)
        {
            return Fail($"could not write {configPath}: {writeError}; and the sentinel {sentinel.Path} could not be " +
                        $"removed either ({removeError}); the archive is still on");
        }
        if (writeError is not null)
        {
            return Fail($"could not write {configPath}: {writeError}; the setting was not changed, but the sentinel " +
                        "was removed, so the archive is off for now; transcripts.enabled is still true and the next " +
                        "engine start turns it on again, so fix the file and run disable again");
        }
        if (removeError is not null)
        {
            return Fail($"transcripts.enabled is now false, but the sentinel {sentinel.Path} could not be removed " +
                        $"({removeError}); the next engine start removes it");
        }

        Out.WriteLine("the transcript archive is disabled");
        Out.WriteLine("what it holds is kept; 'total-recall transcripts forget --all' deletes it");
        return 0;
    }
}
