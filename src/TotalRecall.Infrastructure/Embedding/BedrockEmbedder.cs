using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TotalRecall.Infrastructure.Embedding;

public sealed class BedrockEmbedder : IEmbedder
{
    private readonly string _invokeUrl;
    private readonly string _apiKey;
    private readonly int _dimensions;
    private readonly string _model;
    private readonly HttpClient _http;

    public BedrockEmbedder(
        string region, string apiKey, string model, int dimensions,
        HttpClient? httpClient = null)
    {
        _invokeUrl = $"https://bedrock-runtime.{region}.amazonaws.com/model/{model}/invoke";
        _apiKey = apiKey;
        _dimensions = dimensions;
        _model = model;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <inheritdoc />
    public EmbedderDescriptor Descriptor =>
        new(Provider: "bedrock", Model: _model, Revision: string.Empty, Dimensions: _dimensions);

    public float[] Embed(string text)
    {
        var payload = new BedrockEmbedRequest(
            Texts: new[] { text },
            InputType: "search_document",
            EmbeddingTypes: new[] { "float" },
            OutputDimension: _dimensions);

        // Source-gen serialization — AOT-safe, no IL2026/IL3050 warnings.
        var body = JsonSerializer.Serialize(payload, EmbeddingJsonContext.Default.BedrockEmbedRequest);
        var request = new HttpRequestMessage(HttpMethod.Post, _invokeUrl)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = _http.Send(request);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream();
        var parsed = JsonSerializer.Deserialize(stream, EmbeddingJsonContext.Default.BedrockEmbedResponse)
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
}
