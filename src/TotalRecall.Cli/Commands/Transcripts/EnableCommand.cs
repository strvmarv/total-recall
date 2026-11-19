using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts enable</c>: turns the transcript archive on (spec section 8). Refused under postgres
/// storage; needs a terminal and a confirmation. The order means a failure before the setting is written leaves the
/// setting and the sentinel as they were: open the store creating its tables, then write <c>transcripts.enabled = true</c>
/// (with the writer: the guard against <c>config set</c> lives in its callers), then create the sentinel. When only the
/// sentinel fails, the setting stays true and the next engine start writes the sentinel.
/// </summary>
public sealed class EnableCommand : TranscriptsCommandBase
{
    public EnableCommand() { }

    public EnableCommand(
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

    public override string Name => "enable";
    public override string Description => "Turn the transcript archive on (asks first; run it in a terminal)";
    protected override string Usage => "Usage: total-recall transcripts enable";

    protected override int Execute(string[] args)
    {
        if (RejectArguments(args) is { } usage)
            return usage;

        string storageMode;
        try
        {
            storageMode = ReadStorageMode();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"could not read the configuration: {ex.Message}");
        }
        if (string.Equals(storageMode, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("the transcript archive is not supported with postgres storage " +
                        "(storage.mode or storage.connection_string selects it); it needs the local SQLite database");
        }

        if (RequireTerminal() is { } noTerminal)
            return noTerminal;

        var sentinel = Sentinel;
        var configPath = UserConfigPath;
        if (!RequireAbsolute(configPath) || !RequireAbsolute(sentinel.Path))
            return 1;

        var location = ResolveLocation(ReadSettings());
        if (location is null)
            return 1;

        var where = Describe(location);
        var prompt =
            "Enable the transcript archive? Once a later version can fill it, total-recall will keep the dialogue " +
            "of your agent sessions (your prompts and the assistant's replies) in " + The(location) + ", locally on this " +
            "machine, with secrets redacted, and never synced. Nothing is stored yet in this version. " +
            "You can delete it at any time with 'total-recall transcripts forget'. Continue?";
        if (!Confirm(prompt))
        {
            Out.WriteLine("aborted; nothing was changed");
            return 0;
        }

        var opened = TranscriptStore.Open(location, create: true);
        if (opened.Status != TranscriptOpenStatus.Opened || opened.Store is null)
        {
            opened.Store?.Dispose();
            return Fail($"could not open the archive at {where}: {opened.Message ?? opened.Status.ToString()}; nothing was changed");
        }
        opened.Store.Dispose();

        try
        {
            ConfigWriter.SaveUserOverride(configPath, "transcripts.enabled", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"could not write {configPath}: {ex.Message}; the setting was not changed");
        }

        try
        {
            sentinel.Write();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"transcripts.enabled is now true, but the sentinel {sentinel.Path} could not be written " +
                        $"({ex.Message}); the next engine start writes it");
        }

        Out.WriteLine("the transcript archive is enabled");
        Out.WriteLine($"location: {where}");
        Out.WriteLine("Nothing is stored yet in this version: a later release fills and searches the archive.");
        return 0;
    }
}
