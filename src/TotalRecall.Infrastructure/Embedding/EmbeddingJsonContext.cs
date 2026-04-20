using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Embedding;

// AOT-safe wire DTOs for the remote embedders (BedrockEmbedder, OpenAiEmbedder).
// Each type is registered on the source-gen context below so that
// JsonSerializer.(De)serialize can round-trip them without the reflection-based
// code paths that fire IL2026/IL3050 under AOT.

internal sealed record BedrockEmbedRequest(
    [property: JsonPropertyName("texts")] string[] Texts,
    [property: JsonPropertyName("input_type")] string InputType,
    [property: JsonPropertyName("embedding_types")] string[] EmbeddingTypes,
    [property: JsonPropertyName("output_dimension")] int OutputDimension);

internal sealed record BedrockEmbedFloats(
    [property: JsonPropertyName("float")] float[][]? FloatVectors);

internal sealed record BedrockEmbedResponse(
    [property: JsonPropertyName("embeddings")] BedrockEmbedFloats? Embeddings);

internal sealed record OpenAiEmbedRequest(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("dimensions")] int Dimensions);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BedrockEmbedRequest))]
[JsonSerializable(typeof(BedrockEmbedResponse))]
[JsonSerializable(typeof(BedrockEmbedFloats))]
[JsonSerializable(typeof(OpenAiEmbedRequest))]
internal partial class EmbeddingJsonContext : JsonSerializerContext
{
}
