using System.Net;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class PublicRepoProbeTests
{
    private static async Task Respond(HttpListenerContext context, int status, string? location = null)
    {
        context.Response.StatusCode = status;
        if (location is not null) context.Response.RedirectLocation = location;
        context.Response.Close();
        await Task.CompletedTask;
    }

    [LoopbackFact]
    public async Task TheRequest_HasTheRightPathAndHeaders_AndNoCredentials()
    {
        using var server = new LoopbackServer(c => Respond(c, 200));
        using var probe = new PublicRepoProbe(server.BaseAddress);

        var status = await probe.GetStatusAsync("o", "r", CancellationToken.None);

        Assert.Equal(200, status);
        var (method, path, headers) = Assert.Single(server.Seen);
        Assert.Equal("GET", method);
        Assert.Equal("/repos/o/r", path);
        Assert.Null(headers["Authorization"]);
        Assert.Null(headers["Cookie"]);
        Assert.Equal("application/vnd.github+json", headers["Accept"]);
        Assert.StartsWith("total-recall", headers["User-Agent"]);
    }

    [LoopbackFact]
    public async Task A404_IsReturnedAs404()
    {
        using var server = new LoopbackServer(c => Respond(c, 404));
        using var probe = new PublicRepoProbe(server.BaseAddress);

        Assert.Equal(404, await probe.GetStatusAsync("o", "r", CancellationToken.None));
    }

    [LoopbackFact]
    public async Task ARelativeRedirect_IsFollowed()
    {
        using var server = new LoopbackServer(c =>
            c.Request.Url!.AbsolutePath == "/repos/o/r" ? Respond(c, 301, "/repositories/1") : Respond(c, 200));
        using var probe = new PublicRepoProbe(server.BaseAddress);

        Assert.Equal(200, await probe.GetStatusAsync("o", "r", CancellationToken.None));
        Assert.Equal(2, server.Seen.Count);
    }

    [LoopbackFact]
    public async Task FourChainedRedirects_EndOnTheRedirectStatus()
    {
        using var server = new LoopbackServer(c =>
        {
            var path = c.Request.Url!.AbsolutePath;
            var hop = path.StartsWith("/hop", StringComparison.Ordinal) ? int.Parse(path[4..]) : 0;
            return Respond(c, 301, $"/hop{hop + 1}");
        });
        using var probe = new PublicRepoProbe(server.BaseAddress);

        Assert.Equal(301, await probe.GetStatusAsync("o", "r", CancellationToken.None));
    }

    [LoopbackFact]
    public async Task AListenerThatNeverAnswers_IsNull_WithinTheProbesLimit()
    {
        using var gate = new ManualResetEventSlim();
        using var server = new LoopbackServer(_ => Task.Run(() => gate.Wait(5000)));
        using var probe = new PublicRepoProbe(server.BaseAddress, null, TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        var status = await probe.GetStatusAsync("o", "r", CancellationToken.None);
        gate.Set();

        Assert.Null(status);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [LoopbackFact]
    public async Task ACancellationFromTheCaller_Propagates()
    {
        using var gate = new ManualResetEventSlim();
        using var server = new LoopbackServer(_ => Task.Run(() => gate.Wait(5000)));
        using var probe = new PublicRepoProbe(server.BaseAddress);
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetStatusAsync("o", "r", cts.Token));
        gate.Set();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task AResponseFromAnotherHost_IsNull()
    {
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/x"),
        });
        using var probe = new PublicRepoProbe(null, handler);

        Assert.Null(await probe.GetStatusAsync("o", "r", CancellationToken.None));
    }

    [Fact]
    public async Task AResponseFromTheBaseHost_IsReturned_AndTheRequestIsAnonymous()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
        using var probe = new PublicRepoProbe(null, handler);

        Assert.Equal(404, await probe.GetStatusAsync("o", "r", CancellationToken.None));
        Assert.Equal("https://api.github.com/repos/o/r", seen!.RequestUri!.ToString());
        Assert.Null(seen.Headers.Authorization);
    }

    [Fact]
    public async Task AnHttpRequestException_IsNull()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        using var probe = new PublicRepoProbe(null, handler);

        Assert.Null(await probe.GetStatusAsync("o", "r", CancellationToken.None));
    }
}
