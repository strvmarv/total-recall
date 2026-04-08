// src/TotalRecall.Cli/Internal/EmbedderFactory.cs
//
// Plan 5 Task 5.3b — shared helper for CLI commands that need a real
// OnnxEmbedder in production. Extracted from the two duplicated copies
// that landed in 5.2 (MigrateCommand.BuildProductionMigrator) and
// 5.3a (EvalEmbedderFactory.Build). Walks AppContext.BaseDirectory
// upward to find bundled models/, loads the registry, points the
// user override dir at $HOME/.total-recall/models, and constructs
// an OnnxEmbedder for "all-MiniLM-L6-v2".

using System;
using System.IO;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Cli.Internal;

internal static class EmbedderFactory
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
