// src/TotalRecall.Cli/Commands/Eval/EvalEmbedderFactory.cs
//
// Plan 5 Task 5.3a — small shared helper that mirrors the model bootstrap
// path used by MigrateCommand.BuildProductionMigrator. Both eval CLI verbs
// (BenchmarkCommand + ReportCommand if it ever needs an embedder) and any
// future Plan 5 leaf that needs an OnnxEmbedder reuse this rather than
// re-inlining the AppContext.BaseDirectory walk.

using System;
using System.IO;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Cli.Commands.Eval;

internal static class EvalEmbedderFactory
{
    public static OnnxEmbedder Build()
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
