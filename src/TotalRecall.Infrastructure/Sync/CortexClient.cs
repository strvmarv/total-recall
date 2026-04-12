using System.Text;
using System.Text.Json;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// HTTP client for the Cortex sync API. Implements <see cref="IRemoteBackend"/>
/// by mapping each operation to a REST endpoint under /api/plugin/sync/.
/// </summary>
public sealed class CortexClient : IRemoteBackend
{
    private readonly HttpClient _http;

    public CortexClient(HttpClient http) => _http = http;

    /// <summary>
    /// Factory that creates a <see cref="CortexClient"/> with a pre-configured
    /// <see cref="HttpClient"/> (base address, bearer token, timeout).
    /// </summary>
    public static CortexClient Create(string url, string pat, TimeSpan? timeout = null)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(url.TrimEnd('/')),
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        return new CortexClient(http);
    }

    public async Task<SyncSearchResult[]> SearchKnowledgeAsync(string query, int topK, CancellationToken ct)
    {
        var url = $"/api/plugin/sync/knowledge?query={Uri.EscapeDataString(query)}&top_k={topK}";
        return await GetAsync(url, SyncJsonContext.Default.SyncSearchResultArray, ct).ConfigureAwait(false)
               ?? Array.Empty<SyncSearchResult>();
    }

    public async Task<SyncSearchResult[]> SearchMemoriesAsync(string query, string scope, int topK, CancellationToken ct)
    {
        var url = $"/api/plugin/sync/memories/search?query={Uri.EscapeDataString(query)}&scope={Uri.EscapeDataString(scope)}&top_k={topK}";
        return await GetAsync(url, SyncJsonContext.Default.SyncSearchResultArray, ct).ConfigureAwait(false)
               ?? Array.Empty<SyncSearchResult>();
    }

    public async Task<SyncStatusResult> GetStatusAsync(CancellationToken ct)
    {
        var url = "/api/plugin/sync/status";
        return await GetAsync(url, SyncJsonContext.Default.SyncStatusResult, ct).ConfigureAwait(false)
               ?? new SyncStatusResult(0, 0, 0, 0);
    }

    public async Task UpsertMemoriesAsync(SyncEntry[] entries, CancellationToken ct)
    {
        await PostAsync("/api/plugin/sync/memories", entries, SyncJsonContext.Default.SyncEntryArray, ct).ConfigureAwait(false);
    }

    public async Task DeleteMemoryAsync(string id, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Delete, $"/api/plugin/sync/memories/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
    }

    public async Task<SyncPullResult> GetUserMemoriesModifiedSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var iso = since.UtcDateTime.ToString("o");
        var url = $"/api/plugin/sync/memories?since={Uri.EscapeDataString(iso)}";
        return await GetAsync(url, SyncJsonContext.Default.SyncPullResult, ct).ConfigureAwait(false)
               ?? new SyncPullResult(Array.Empty<SyncEntry>(), null);
    }

    public async Task PushUsageEventsAsync(SyncUsageEvent[] events, CancellationToken ct)
    {
        await PostAsync("/api/plugin/sync/usage", events, SyncJsonContext.Default.SyncUsageEventArray, ct).ConfigureAwait(false);
    }

    public async Task PushRetrievalEventsAsync(SyncRetrievalEvent[] events, CancellationToken ct)
    {
        await PostAsync("/api/plugin/sync/retrieval", events, SyncJsonContext.Default.SyncRetrievalEventArray, ct).ConfigureAwait(false);
    }

    public async Task PushCompactionEntriesAsync(SyncCompactionEntry[] entries, CancellationToken ct)
    {
        await PostAsync("/api/plugin/sync/compaction", entries, SyncJsonContext.Default.SyncCompactionEntryArray, ct).ConfigureAwait(false);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CortexUnreachableException($"Cortex API request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CortexUnreachableException("Cortex API request timed out.", ex);
        }
    }

    private async Task PostAsync<T>(string url, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, typeInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new CortexUnreachableException($"Cortex API request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CortexUnreachableException("Cortex API request timed out.", ex);
        }
    }

    private async Task SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new CortexUnreachableException($"Cortex API request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CortexUnreachableException("Cortex API request timed out.", ex);
        }
    }
}
