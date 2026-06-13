namespace TotalRecall.Web.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using TotalRecall.Server;
using TotalRecall.Web;
using TotalRecall.Web.Tests.TestSupport;
using Xunit;

public sealed class ApiIntegrationTests : IAsyncLifetime
{
    private const string Token = "test-token-123";
    private Microsoft.AspNetCore.Builder.WebApplication _app = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeToolHandler("status", """{"ok":true}"""));
        registry.Register(new FakeToolHandler("migrate_to_remote", """{"x":1}"""));
        registry.Register(new FakeToolHandler("eval_report", """{"err":1}""", isError: true));

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

    private HttpRequestMessage Req(HttpMethod m, string path, bool withToken = true)
    {
        var r = new HttpRequestMessage(m, path);
        if (withToken) r.Headers.Add("X-Total-Recall-Token", Token);
        return r;
    }

    [Fact]
    public async Task Health_IsOpen_NoTokenRequired()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Get, "/api/health", withToken: false));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"backend\":\"sqlite\"", body);
    }

    [Fact]
    public async Task Dispatch_AllowedTool_ReturnsHandlerJson()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Post, "/api/tool/status"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("""{"ok":true}""", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dispatch_NotAllowlisted_Returns404()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Post, "/api/tool/migrate_to_remote"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("unknown_tool", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dispatch_UnknownTool_Returns404()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Post, "/api/tool/nope"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Api_MissingToken_Returns401()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Post, "/api/tool/status", withToken: false));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Api_BadHostHeader_Returns403()
    {
        var r = Req(HttpMethod.Post, "/api/tool/status");
        r.Headers.Host = "evil.com";
        var resp = await _client.SendAsync(r);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Root_ServesIndex_WithInjectedBootstrap()
    {
        var resp = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("total-recall web UI", body);            // placeholder marker (test build is Node-free)
        Assert.Contains("window.__TR_BOOTSTRAP__", body);        // bootstrap injected
        Assert.Contains("\"token\":\"" + Token + "\"", body);    // the per-launch token
        Assert.Contains("\"backend\":\"sqlite\"", body);
    }

    [Fact]
    public async Task Index_IsNotCached()
    {
        var resp = await _client.GetAsync("/");
        Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task DeepLink_FallsBackToIndex()
    {
        var resp = await _client.GetAsync("/memory");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("window.__TR_BOOTSTRAP__", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownApiRoute_Returns404Json_NotHtml()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Get, "/api/does-not-exist"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Dispatch_HandlerError_Returns400()
    {
        var resp = await _client.SendAsync(Req(HttpMethod.Post, "/api/tool/eval_report"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dispatch_MalformedJsonBody_Returns400()
    {
        var r = Req(HttpMethod.Post, "/api/tool/status");
        r.Content = new StringContent("{not valid", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(r);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("invalid_json", await resp.Content.ReadAsStringAsync());
    }
}
