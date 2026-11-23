// The production wiring of the git sync setup commands: reads the configuration and resolves the paths when a command
// runs, and turns a failure to do so into a LoadError the service reports (init, join and leave answer `failed`; status
// prints unknown for the facts that need it). Milestone 1b-iii part 2 (held deletions, D1): the review environment adds
// the embedder and the tier limits from the same configuration.

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Memory;

namespace TotalRecall.Cli.Commands.Sync;

internal static class SyncSetupFactory
{
    /// <summary>Builds the environment; the caller disposes <paramref name="probe"/> when the command ends.</summary>
    public static SyncSetupEnvironment Create(Action<string> report, out PublicRepoProbe probe) =>
        Build(report, out probe, out _);

    /// <summary>
    /// Builds the review environment over the setup environment of <see cref="Create"/>: the hot tier limit, the cap of a
    /// shown text (<c>tiers.pinned.max_content_chars</c>, else the pinned default) and an embedder created from the
    /// configuration on first use. When the configuration could not be read the limits are none and the pinned default,
    /// and the embedder throws the load error (the service answers <c>failed</c> on a load error before it embeds). The
    /// caller disposes <paramref name="probe"/> when the command ends.
    /// </summary>
    public static ReviewEnvironment CreateReview(Action<string> report, out PublicRepoProbe probe)
    {
        var setup = Build(report, out probe, out var cfg);
        if (cfg is null)
        {
            var loadError = setup.LoadError;
            return new ReviewEnvironment(
                setup, () => throw new InvalidOperationException(loadError), 0, PinnedTierLimits.DefaultMaxContentChars);
        }
        var pinnedMax = FSharpOption<Core.Config.PinnedTierConfig>.get_IsSome(cfg.Tiers.Pinned)
            ? cfg.Tiers.Pinned.Value.MaxContentChars
            : PinnedTierLimits.DefaultMaxContentChars;
        return new ReviewEnvironment(setup, () => EmbedderFactory.CreateFromConfig(cfg.Embedding), cfg.Tiers.Hot.MaxContentChars, pinnedMax);
    }

    /// <summary>The environment, and the loaded configuration (null when it could not be read).</summary>
    private static SyncSetupEnvironment Build(Action<string> report, out PublicRepoProbe probe, out Core.Config.TotalRecallConfig? config)
    {
        string? loadError = null;
        config = null;
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
            config = cfg;
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
