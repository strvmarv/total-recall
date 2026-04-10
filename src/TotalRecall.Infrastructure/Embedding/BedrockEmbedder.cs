using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Embedding;

public sealed class BedrockEmbedder : IEmbedder
{
    private readonly string _invokeUrl;
    private readonly string _apiKey;
    private readonly int _dimensions;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public BedrockEmbedder(
        string region, string apiKey, string model, int dimensions,
        HttpClient? httpClient = null)
    {
        _invokeUrl = $"https://bedrock-runtime.{region}.amazonaws.com/model/{model}/invoke";
        _apiKey = apiKey;
        _dimensions = dimensions;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public float[] Embed(string text)
    {
        var payload = new EmbedRequest(
            Texts: new[] { text },
            InputType: "search_document",
            EmbeddingTypes: new[] { "float" },
            OutputDimension: _dimensions);

        var body = JsonSerializer.Serialize(payload, SnakeCaseOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, _invokeUrl)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = _http.Send(request);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream();
        var parsed = JsonSerializer.Deserialize<EmbedResponse>(stream, SnakeCaseOptions)
            ?? throw new InvalidOperationException("Bedrock returned null response");

        var vector = parsed.Embeddings?.FloatVectors?[0]
            ?? throw new InvalidOperationException("Bedrock response missing float embeddings");

        return L2Normalize(vector);
    }

    private static float[] L2Normalize(float[] vec)
    {
        var sumSq = 0.0;
        for (var i = 0; i < vec.Length; i++) sumSq += vec[i] * vec[i];
        if (sumSq <= 0) return vec;
        var norm = (float)Math.Sqrt(sumSq);
        for (var i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }

    private record EmbedRequest(string[] Texts, string InputType, string[] EmbeddingTypes, int OutputDimension);
    private record EmbedFloats([property: JsonPropertyName("float")] float[][]? FloatVectors);
    private record EmbedResponse(EmbedFloats? Embeddings);
}
