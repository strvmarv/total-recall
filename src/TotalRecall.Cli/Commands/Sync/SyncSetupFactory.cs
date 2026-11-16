// The production wiring of the git sync setup commands: reads the configuration and resolves the paths when a command
// runs, and turns a failure to do so into a LoadError the service reports (init, join and leave answer `failed`; status
// prints unknown for the facts that need it).

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

internal static class SyncSetupFactory
{
    /// <summary>Builds the environment; the caller disposes <paramref name="probe"/> when the command ends.</summary>
    public static SyncSetupEnvironment Create(Action<string> report, out PublicRepoProbe probe)
    {
        string? loadError = null;
        var settings = Core.Config.GitSyncSettings.defaults;
        var mode = "local";
        var dbPath = Path.Combine(Path.GetTempPath(), "total-recall-unknown.db");
        var configPath = Path.Combine(Path.GetTempPath(), "total-recall-unknown.toml");

        try
        {
            var cfg = new ConfigLoader().LoadEffectiveConfig();
            if (FSharpOption<Core.Config.GitSyncConfig>.get_IsSome(cfg.GitSync))
                settings = cfg.GitSync.Value;
            var storage = FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage) ? cfg.Storage.Value : null;
            mode = SyncSetup.effectiveMode(storage?.Mode ?? FSharpOption<string>.None, storage?.ConnectionString ?? FSharpOption<string>.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            loadError = $"the configuration could not be read: {ex.Message}";
        }

        try
        {
            dbPath = ConfigLoader.GetDbPath();
            configPath = Path.Combine(ConfigLoader.GetDataDir(), "config.toml");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            loadError ??= $"the database path could not be resolved: {ex.Message}";
        }

        probe = new PublicRepoProbe();
        var caseInsensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        return new SyncSetupEnvironment(
            dbPath,
            caseInsensitive,
            settings,
            mode,
            PathVariable: null,
            new GitProcess(),
            GitOptions: null,
            probe,
            new UserConfigFile(configPath),
            HostIdentity.ForCurrentMachine(dbPath),
            () => DateTimeOffset.UtcNow,
            RandomNumberGenerator.GetBytes,
            CliApp.CurrentVersion,
            report,
            Fault: null,
            Move: null,
            loadError);
    }
}
