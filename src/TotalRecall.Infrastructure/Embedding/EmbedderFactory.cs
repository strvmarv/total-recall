// src/TotalRecall.Infrastructure/Embedding/EmbedderFactory.cs
//
// Plan 6 Task 6.0c — promoted from TotalRecall.Cli.Internal.EmbedderFactory
// so that TotalRecall.Server can build an OnnxEmbedder without taking a
// reference on Cli (Server must not reference Cli). The Cli-side shim now
// forwards to this implementation — see src/TotalRecall.Cli/Internal/
// EmbedderFactory.cs.

using System;
using System.IO;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Walks AppContext.BaseDirectory upward to find bundled <c>models/</c>,
/// loads the registry, points the user override dir at
/// <c>$HOME/.total-recall/models</c>, and constructs an
/// <see cref="OnnxEmbedder"/> for "all-MiniLM-L6-v2".
/// </summary>
public static class EmbedderFactory
{
    public static OnnxEmbedder CreateProduction()
    {
        var bundledModelsDir = FindBundledModelsDir();
        var registryPath = Path.Combine(bundledModelsDir, "registry.json");
        var registry = ModelRegistry.LoadFromFile(registryPath);

        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Path.GetTempPath();
        var userModelsDir = Path.Combine(home, ".total-recall", "models");
        Directory.CreateDirectory(userModelsDir);

        var manager = new ModelManager(registry, bundledModelsDir, userModelsDir);
        return new OnnxEmbedder(manager, "all-MiniLM-L6-v2");
    }

    private static string FindBundledModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "registry.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "models");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate bundled models/ directory (walked up from "
            + AppContext.BaseDirectory + " looking for models/registry.json)");
    }
}
