using System.Text.Json.Serialization;

namespace TotalRecall.Web.Api;

/// <summary>Uniform error envelope for /api/* failures. `detail` is omitted from the wire when null.</summary>
public sealed record ApiError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);
