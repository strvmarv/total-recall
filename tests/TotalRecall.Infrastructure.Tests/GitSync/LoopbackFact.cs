using System.Net;
using System.Net.Sockets;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>A fact that needs a loopback <see cref="HttpListener"/>; skipped with a reason when one cannot be bound.</summary>
internal sealed class LoopbackFactAttribute : FactAttribute
{
    public LoopbackFactAttribute()
    {
        try
        {
            using var server = new LoopbackServer(_ => Task.CompletedTask);
        }
        catch (Exception ex) when (ex is HttpListenerException or SocketException or PlatformNotSupportedException)
        {
            Skip = "cannot bind a loopback HttpListener: " + ex.Message;
        }
    }
}

/// <summary>A loopback HTTP server for the probe tests: each request is handed to <c>handler</c>, which writes the response.</summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly Task _loop;

    public List<(string Method, string Path, System.Collections.Specialized.NameValueCollection Headers)> Seen { get; } = [];
    public Uri BaseAddress { get; }

    public LoopbackServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        var port = FreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            lock (Seen) Seen.Add((context.Request.HttpMethod, context.Request.Url!.AbsolutePath, new System.Collections.Specialized.NameValueCollection(context.Request.Headers)));
            try { await _handler(context).ConfigureAwait(false); }
            catch (Exception) { try { context.Response.Abort(); } catch (Exception) { } }
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch (Exception) { }
        try { _loop.Wait(1000); } catch (Exception) { }
    }
}
