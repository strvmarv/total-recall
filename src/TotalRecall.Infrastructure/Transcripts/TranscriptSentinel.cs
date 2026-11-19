using System;
using System.IO;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The marker file <c>&lt;dataDir&gt;/live/enabled</c> that tells the host-side hooks (which run
/// without the engine) whether the transcript archive is on. It mirrors the engine's decision:
/// present when the archive is enabled and the store is not postgres, absent otherwise.
/// </summary>
public sealed class TranscriptSentinel
{
    public TranscriptSentinel(string dataDir)
    {
        Path = System.IO.Path.Combine(dataDir, "live", "enabled");
    }

    /// <summary><c>&lt;dataDir&gt;/live/enabled</c>.</summary>
    public string Path { get; }

    public bool IsEnabled() => File.Exists(Path);

    /// <summary>Creates the sentinel (<c>live/</c> 0700, file 0600 on Unix); idempotent. May throw.</summary>
    public void Write()
    {
        PrivateFiles.EnsureDirectory(System.IO.Path.GetDirectoryName(Path)!);
        PrivateFiles.CreateEmptyFile(Path);
    }

    /// <summary>Deletes the sentinel; a missing file is not an error. May throw on other I/O failures.</summary>
    public void Remove()
    {
        try { File.Delete(Path); }
        catch (DirectoryNotFoundException) { /* nothing to remove */ }
    }

    /// <summary>
    /// The storage mode in effect, by the rule of <c>ServerComposition.OpenConfigured</c>:
    /// <c>storage.mode</c> when set, else "postgres" when <c>storage.connection_string</c> is set, else "local".
    /// </summary>
    public static string EffectiveStorageMode(Core.Config.TotalRecallConfig cfg)
    {
        if (FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage))
        {
            var storage = cfg.Storage.Value;
            if (FSharpOption<string>.get_IsSome(storage.Mode))
                return storage.Mode.Value;
            if (FSharpOption<string>.get_IsSome(storage.ConnectionString))
                return "postgres";
        }
        return "local";
    }

    /// <summary>The archive is active when it is enabled and the store is not postgres.</summary>
    public static bool IsActive(bool settingEnabled, string storageMode) =>
        settingEnabled && !string.Equals(storageMode, "postgres", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings the sentinel in line with the setting: written when active, removed when not.
    /// Never throws; every failure is logged.
    /// </summary>
    public void Mirror(bool settingEnabled, string storageMode)
    {
        try
        {
            if (IsActive(settingEnabled, storageMode)) Write();
            else Remove();
        }
        catch (Exception ex)
        {
            try { ExceptionLogger.LogChain("transcripts sentinel", ex); } catch { /* logging must not throw */ }
        }
    }
}
