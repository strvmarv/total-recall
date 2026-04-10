using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TotalRecall.Infrastructure.Embedding;

public sealed class OpenAiEmbedder : IEmbedder
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _modelName;
    private readonly int _dimensions;
    private readonly HttpClient _http;

    public OpenAiEmbedder(
        string endpoint, string apiKey, string modelName, int dimensions,
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        _modelName = modelName;
        _dimensions = dimensions;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public float[] Embed(string text)
    {
        var body = JsonSerializer.Serialize(new
        {
            input = text,
            model = _modelName,
            dimensions = _dimensions
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/embeddings")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = _http.Send(request);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream();
        using var doc = JsonDocument.Parse(stream);
        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        var result = new float[embeddingArray.GetArrayLength()];
        var i = 0;
        foreach (var el in embeddingArray.EnumerateArray())
            result[i++] = el.GetSingle();

        return L2Normalize(result);
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
