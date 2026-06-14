using System.Text.Json.Serialization;

namespace TotalRecall.Web.Api;

/// <summary>
/// Runtime config injected into the served index.html as
/// <c>window.__TR_BOOTSTRAP__</c> so the SPA can authenticate /api/* and
/// label the active storage backend. The token is per-launch (not persisted).
/// </summary>
public sealed record BootstrapInfo(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("version")] string Version);
