using System.Text.Json.Serialization;

namespace TotalRecall.Web.Api;

/// <summary>Response shape for GET /api/health.</summary>
public sealed record HealthInfo(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("version")] string Version);
