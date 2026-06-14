namespace TotalRecall.Web.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server;
using TotalRecall.Web;
using Xunit;

/// <summary>
/// Verifies that the /api/tool/{name} dispatch endpoint routes handler-thrown
/// exceptions through ErrorTranslator and returns HTTP 400 (not 500).
/// </summary>
public sealed class DispatchErrorTests : IAsyncLifetime
{
    private const string Token = "test-token-dispatch-error";

    private Microsoft.AspNetCore.Builder.WebApplication _app = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        // Register a stub under "status" (already allowlisted) that throws
        // ArgumentException — the classic "bad input" exception that memory
        // write-tools raise on invalid arguments.
        var registry = new ToolRegistry();
        registry.Register(new ThrowingHandler("status", new ArgumentException("bad id")));

        var options = new WebUiOptions(Port: 0, Host: "127.0.0.1", OpenBrowser: false, Token: Token, Smoke: false);
        _app = WebUiServer.BuildApp(options, registry, Token, backendLabel: "sqlite", version: "test");
        await _app.StartAsync();
        var baseUrl = WebUiServer.ResolveBoundUrl(_app);
        _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_HandlerThrowsArgumentException_Returns400WithMessage()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/tool/status");
        req.Headers.Add("X-Total-Recall-Token", Token);

        var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("bad id", body);
    }

    /// <summary>
    /// Stub IToolHandler that always throws the supplied exception from ExecuteAsync.
    /// </summary>
    private sealed class ThrowingHandler : IToolHandler
    {
        private readonly Exception _ex;

        public ThrowingHandler(string name, Exception ex)
        {
            Name = name;
            _ex = ex;
        }

        public string Name { get; }
        public string Description => "throwing stub";

        private static readonly JsonElement _schema =
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public JsonElement InputSchema => _schema;

        public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct) =>
            throw _ex;
    }
}
