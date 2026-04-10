// src/TotalRecall.Infrastructure/Embedding/EmbedderFactory.cs
//
// Plan 6 Task 6.0c — promoted from TotalRecall.Cli.Internal.EmbedderFactory
// so that TotalRecall.Server can build an OnnxEmbedder without taking a
// reference on Cli (Server must not reference Cli). The Cli-side shim now
// forwards to this implementation — see src/TotalRecall.Cli/Internal/
// EmbedderFactory.cs.

using System;
using System.IO;
using Microsoft.FSharp.Core;

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

    /// <summary>
    /// Create an embedder based on configuration. Provider selection:
    /// - absent/"local" → OnnxEmbedder (bundled model)
    /// - "openai" → OpenAiEmbedder
    /// - "bedrock" → BedrockEmbedder
    /// </summary>
    public static IEmbedder CreateFromConfig(Core.Config.EmbeddingConfig cfg)
    {
        var provider = FSharpOption<string>.get_IsSome(cfg.Provider)
            ? cfg.Provider.Value : "local";

        switch (provider.ToLowerInvariant())
        {
            case "local":
                return CreateProduction();

            case "openai":
            {
                var endpoint = FSharpOption<string>.get_IsSome(cfg.Endpoint)
                    ? cfg.Endpoint.Value
                    : throw new InvalidOperationException("embedding.endpoint required when provider = 'openai'");
                var modelName = FSharpOption<string>.get_IsSome(cfg.ModelName)
                    ? cfg.ModelName.Value : cfg.Model;
                var apiKey = ResolveApiKey(cfg);
                return new OpenAiEmbedder(endpoint, apiKey, modelName, cfg.Dimensions);
            }

            case "bedrock":
            {
                var region = FSharpOption<string>.get_IsSome(cfg.BedrockRegion)
                    ? cfg.BedrockRegion.Value
                    : throw new InvalidOperationException("embedding.bedrock_region required when provider = 'bedrock'");
                var model = FSharpOption<string>.get_IsSome(cfg.BedrockModel)
                    ? cfg.BedrockModel.Value
                    : throw new InvalidOperationException("embedding.bedrock_model required when provider = 'bedrock'");
                var apiKey = ResolveApiKey(cfg);
                return new BedrockEmbedder(region, apiKey, model, cfg.Dimensions);
            }

            default:
                throw new InvalidOperationException(
                    $"Unknown embedding provider: '{provider}'. Expected 'local', 'openai', or 'bedrock'.");
        }
    }

    private static string ResolveApiKey(Core.Config.EmbeddingConfig cfg)
    {
        if (FSharpOption<string>.get_IsSome(cfg.ApiKey))
            return cfg.ApiKey.Value;
        var envKey = Environment.GetEnvironmentVariable("TOTAL_RECALL_EMBEDDING_API_KEY");
        return envKey ?? throw new InvalidOperationException(
            "API key required: set embedding.api_key in config or TOTAL_RECALL_EMBEDDING_API_KEY env var");
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
