using System.Net;
using System.Net.Http.Headers;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>Asks whether a GitHub repository is visible without credentials. Returns the final HTTP status, or null for no answer.</summary>
public interface IPublicRepoProbe
{
    Task<int?> GetStatusAsync(string owner, string repo, CancellationToken cancellationToken);
}

/// <summary>
/// <c>GET &lt;base&gt;/repos/&lt;owner&gt;/&lt;repo&gt;</c> with no credentials (spec decision 7): no <c>Authorization</c>
/// header, no cookies, no default credentials, at most 3 redirects, the headers only, a 5-second limit. The answer
/// counts only when the final response came from the host of the base address (<c>api.github.com</c> in production);
/// a network error, a timeout or a response from another host is null. A cancellation from the caller propagates.
/// </summary>
public sealed class PublicRepoProbe : IPublicRepoProbe, IDisposable
{
    private static readonly Uri GitHubApi = new("https://api.github.com/");
    private readonly Uri _base;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public PublicRepoProbe(Uri? baseAddress = null, HttpMessageHandler? handler = null)
        : this(baseAddress, handler, null)
    {
    }

    internal PublicRepoProbe(Uri? baseAddress, HttpMessageHandler? handler, TimeSpan? timeout)
    {
        var baseUri = baseAddress ?? GitHubApi;
        // `new Uri(base, "repos/...")` drops the last segment of a base without a trailing slash
        _base = baseUri.AbsolutePath.EndsWith('/') ? baseUri : new UriBuilder(baseUri) { Path = baseUri.AbsolutePath + "/" }.Uri;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        handler ??= new HttpClientHandler
        {
            UseCookies = false,
            UseDefaultCredentials = false,
            UseProxy = !_base.IsLoopback, // the system proxy for api.github.com; never for a loopback test server
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<int?> GetStatusAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var uri = new Uri(_base, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("total-recall", null));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var finalHost = (response.RequestMessage?.RequestUri ?? uri).Host;
            if (!string.Equals(finalHost, _base.Host, StringComparison.OrdinalIgnoreCase)) return null;
            return (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // the probe's own time limit
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
