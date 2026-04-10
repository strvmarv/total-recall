using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class BedrockEmbedderTests
{
    private static string MakeBedrockResponse(float[] values)
    {
        var floatList = string.Join(",", values);
        return $"{{\"embeddings\":{{\"float\":[[{floatList}]]}}}}";
    }

    [Fact]
    public void Embed_SendsCohereBatchRequestAndParsesResponse()
    {
        var rawEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var handler = new FakeHttpHandler(MakeBedrockResponse(rawEmbedding));
        var http = new HttpClient(handler);

        var embedder = new BedrockEmbedder(
            region: "us-east-1",
            apiKey: "test-key",
            model: "cohere.embed-english-v3",
            dimensions: 3,
            httpClient: http);

        var result = embedder.Embed("hello world");

        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("bedrock-runtime", handler.LastRequestUri!.ToString());
        Assert.Contains("cohere.embed-english-v3", handler.LastRequestUri!.ToString());
        Assert.Equal("Bearer test-key", handler.LastAuthHeader);
        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void Embed_L2NormalizesResult()
    {
        // [3, 4, 0] → norm=5 → [0.6, 0.8, 0.0]
        var rawEmbedding = new float[] { 3.0f, 4.0f, 0.0f };
        var handler = new FakeHttpHandler(MakeBedrockResponse(rawEmbedding));
        var http = new HttpClient(handler);

        var embedder = new BedrockEmbedder(
            region: "us-east-1",
            apiKey: "test-key",
            model: "cohere.embed-english-v3",
            dimensions: 3,
            httpClient: http);

        var result = embedder.Embed("normalize me");

        Assert.Equal(3, result.Length);
        Assert.Equal(0.6f, result[0], precision: 5);
        Assert.Equal(0.8f, result[1], precision: 5);
        Assert.Equal(0.0f, result[2], precision: 5);
    }

    [Fact]
    public void Embed_RequestBodyUsesSnakeCaseAndCorrectFields()
    {
        var rawEmbedding = new float[] { 1.0f, 0.0f };
        var handler = new FakeHttpHandler(MakeBedrockResponse(rawEmbedding));
        var http = new HttpClient(handler);

        var embedder = new BedrockEmbedder(
            region: "us-east-1",
            apiKey: "test-key",
            model: "cohere.embed-english-v3",
            dimensions: 512,
            httpClient: http);

        embedder.Embed("check body");

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("texts", handler.LastRequestBody);
        Assert.Contains("input_type", handler.LastRequestBody);
        Assert.Contains("embedding_types", handler.LastRequestBody);
        Assert.Contains("output_dimension", handler.LastRequestBody);
        Assert.Contains("512", handler.LastRequestBody);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthHeader { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHttpHandler(string responseBody) => _responseBody = responseBody;

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            LastRequestUri = request.RequestUri;
            LastAuthHeader = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
            => BuildResponse(request);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(BuildResponse(request));
    }
}
