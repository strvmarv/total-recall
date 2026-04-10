using System;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class EmbedderFactoryTests
{
    private static Core.Config.EmbeddingConfig MakeConfig(
        string? provider = null,
        string? endpoint = null,
        string? apiKey = null,
        string? bedrockRegion = null,
        string? bedrockModel = null,
        string? modelName = null,
        int dimensions = 384)
    {
        return new Core.Config.EmbeddingConfig(
            "all-MiniLM-L6-v2",
            dimensions,
            provider is null ? FSharpOption<string>.None : FSharpOption<string>.Some(provider),
            endpoint is null ? FSharpOption<string>.None : FSharpOption<string>.Some(endpoint),
            bedrockRegion is null ? FSharpOption<string>.None : FSharpOption<string>.Some(bedrockRegion),
            bedrockModel is null ? FSharpOption<string>.None : FSharpOption<string>.Some(bedrockModel),
            modelName is null ? FSharpOption<string>.None : FSharpOption<string>.Some(modelName),
            apiKey is null ? FSharpOption<string>.None : FSharpOption<string>.Some(apiKey));
    }

    [Fact]
    public void CreateFromConfig_NoProvider_ReturnsOnnxEmbedder()
    {
        var cfg = MakeConfig(provider: null);
        var embedder = EmbedderFactory.CreateFromConfig(cfg);
        Assert.IsType<OnnxEmbedder>(embedder);
    }

    [Fact]
    public void CreateFromConfig_LocalProvider_ReturnsOnnxEmbedder()
    {
        var cfg = MakeConfig(provider: "local");
        var embedder = EmbedderFactory.CreateFromConfig(cfg);
        Assert.IsType<OnnxEmbedder>(embedder);
    }

    [Fact]
    public void CreateFromConfig_OpenAiProvider_ReturnsOpenAiEmbedder()
    {
        var cfg = MakeConfig(
            provider: "openai",
            endpoint: "https://api.openai.com/v1",
            apiKey: "test-key");
        var embedder = EmbedderFactory.CreateFromConfig(cfg);
        Assert.IsType<OpenAiEmbedder>(embedder);
    }

    [Fact]
    public void CreateFromConfig_BedrockProvider_ReturnsBedrockEmbedder()
    {
        var cfg = MakeConfig(
            provider: "bedrock",
            bedrockRegion: "us-east-1",
            bedrockModel: "cohere.embed-english-v3",
            apiKey: "test-key");
        var embedder = EmbedderFactory.CreateFromConfig(cfg);
        Assert.IsType<BedrockEmbedder>(embedder);
    }

    [Fact]
    public void CreateFromConfig_OpenAiMissingEndpoint_Throws()
    {
        var cfg = MakeConfig(provider: "openai", apiKey: "test-key");
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbedderFactory.CreateFromConfig(cfg));
        Assert.Contains("embedding.endpoint", ex.Message);
    }

    [Fact]
    public void CreateFromConfig_BedrockMissingRegion_Throws()
    {
        var cfg = MakeConfig(
            provider: "bedrock",
            bedrockModel: "cohere.embed-english-v3",
            apiKey: "test-key");
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbedderFactory.CreateFromConfig(cfg));
        Assert.Contains("embedding.bedrock_region", ex.Message);
    }
}
