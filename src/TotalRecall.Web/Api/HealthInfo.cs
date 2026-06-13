using System.Text.Json.Serialization;

namespace TotalRecall.Web.Api;

/// <summary>Response shape for GET /api/health.</summary>
public sealed record HealthInfo(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Uniform error envelope for /api/* failures.</summary>
public sealed record ApiError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("detail")] string? Detail);
