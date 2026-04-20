using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// HTTP client for cortex <c>/api/me/skills/*</c>. Mirrors the factory and
/// error-translation pattern used by <see cref="CortexClient"/>: any
/// <see cref="HttpRequestException"/> or non-cancellation
/// <see cref="TaskCanceledException"/> is translated into
/// <see cref="CortexUnreachableException"/>.
/// </summary>
public sealed class CortexSkillClient : ISkillClient
{
    private readonly HttpClient _http;

    public CortexSkillClient(HttpClient http) => _http = http;

    /// <summary>
    /// Factory that creates a <see cref="CortexSkillClient"/> with a
    /// pre-configured <see cref="HttpClient"/> (base address, bearer token,
    /// timeout).
    /// </summary>
    public static CortexSkillClient Create(string url, string pat, TimeSpan? timeout = null)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(url.TrimEnd('/')),
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        return new CortexSkillClient(http);
    }

    public async Task<SkillSearchHitDto[]> SearchAsync(
        string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct)
    {
        // Cortex's /api/me/skills/search filters by caller's VisibleScopes
        // internally — there is no ?scope= param on this endpoint. The
        // parameter is kept on the interface for potential future use.
        _ = scope;
        var url = $"/api/me/skills/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (tags is { Count: > 0 })
        {
            url += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        }
        return await GetAsync(url, SkillJsonContext.Default.SkillSearchHitDtoArray, ct).ConfigureAwait(false)
               ?? Array.Empty<SkillSearchHitDto>();
    }

    public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var url = $"/api/me/skills/{id}";
        return GetOrNullAsync(url, SkillJsonContext.Default.SkillBundleDto, ct);
    }

    public async Task<SkillBundleDto?> GetByNaturalKeyAsync(
        string name, string scope, string scopeId, CancellationToken ct)
    {
        var url = $"/api/me/skills" +
                  $"?name={Uri.EscapeDataString(name)}" +
                  $"&scope={Uri.EscapeDataString(scope)}" +
                  $"&scopeId={Uri.EscapeDataString(scopeId)}";

        var list = await GetAsync(url, SkillJsonContext.Default.SkillListResponseDto, ct).ConfigureAwait(false);
        if (list is null || list.Items.Count == 0)
            return null;

        // The natural-key lookup returns SkillDto (summary shape); we need
        // the full bundle, so follow up with a GET by id.
        return await GetByIdAsync(list.Items[0].Id, ct).ConfigureAwait(false);
    }

    public async Task<SkillListResponseDto> ListAsync(
        string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct)
    {
        var url = $"/api/me/skills?skip={skip}&take={take}";
        if (!string.IsNullOrEmpty(scope))
        {
            url += $"&scope={Uri.EscapeDataString(scope)}";
        }
        if (tags is { Count: > 0 })
        {
            url += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        }
        return await GetAsync(url, SkillJsonContext.Default.SkillListResponseDto, ct).ConfigureAwait(false)
               ?? new SkillListResponseDto(0, skip, take, Array.Empty<SkillDto>());
    }

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        return SendAsync(HttpMethod.Delete, $"/api/me/skills/{id}", ct);
    }

    public async Task<SkillImportSummaryDto[]> ImportAsync(
        string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct)
    {
        var body = new SkillImportRequestDto(adapter, skills);
        return await PostAsync(
            "/api/me/skills/import",
            body,
            SkillJsonContext.Default.SkillImportRequestDto,
            SkillJsonContext.Default.SkillImportSummaryDtoArray,
            ct).ConfigureAwait(false)
            ?? Array.Empty<SkillImportSummaryDto>();
    }

    // ── helpers ───────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct)
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

    private async Task<T?> GetOrNullAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        try
        {
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
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

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, requestTypeInfo);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, responseTypeInfo, ct).ConfigureAwait(false);
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
