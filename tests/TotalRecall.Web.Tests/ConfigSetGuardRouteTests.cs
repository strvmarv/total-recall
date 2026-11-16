namespace TotalRecall.Web.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Tomlyn.Model;
using TotalRecall.Server;
using TotalRecall.Server.Handlers;
using Xunit;

/// <summary>
/// The web UI reaches <c>config_set</c> through the allowlist and the same handler as MCP, so the denylist of the
/// sync settings applies to it. The handler is built with a temporary configuration path so a missing guard cannot
/// write the real configuration.
/// </summary>
public sealed class ConfigSetGuardRouteTests : IAsyncLifetime
{
    private const string Token = "test-token-123";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tr-web-cs-" + Guid.NewGuid().ToString("N"));
    private Microsoft.AspNetCore.Builder.WebApplication _app = default!;
    private HttpClient _client = default!;
    private string ConfigPath => Path.Combine(_tempDir, "config.toml");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var registry = new ToolRegistry();
        registry.Register(new ConfigSetHandler(() => new TomlTable(), ConfigPath));
        var options = new WebUiOptions(Port: 0, Host: "127.0.0.1", OpenBrowser: false, Token: Token, Smoke: false);
        _app = WebUiServer.BuildApp(options, registry, Token, backendLabel: "sqlite", version: "test");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(WebUiServer.ResolveBoundUrl(_app)) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> Post(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/tool/config_set")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Total-Recall-Token", Token);
        return await _client.SendAsync(req);
    }

    [Theory]
    [InlineData("git_sync.remote")]
    [InlineData("storage.mode")]
    [InlineData("storage.connection_string")]
    public async Task ProtectedKey_IsRefused_AndTheFileIsNotWritten(string key)
    {
        var resp = await Post($$"""{"key":"{{key}}","value":"x"}""");

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(key, await resp.Content.ReadAsStringAsync());
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task OrdinaryKey_IsStillWritten()
    {
        var resp = await Post("""{"key":"tiers.hot.max_entries","value":25}""");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(File.Exists(ConfigPath));
    }
}
