using System.Text.Json;

namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// Extracts the <c>sub</c> claim from a JWT Bearer token without adding a
/// System.IdentityModel dependency. The bearer token is presented by the
/// plugin process that stored it — we trust the issuer and only need the
/// subject for building <c>user:{sub}</c> scope filters.
/// </summary>
public sealed class JwtCurrentUserId : ICurrentUserId
{
    private readonly string _bearerToken;

    public JwtCurrentUserId(string bearerToken)
    {
        _bearerToken = bearerToken ?? throw new ArgumentNullException(nameof(bearerToken));
    }

    public string GetUserId()
    {
        var parts = _bearerToken.Split('.');
        if (parts.Length < 2)
            throw new InvalidOperationException("JWT missing payload segment");

        var payload = Base64UrlDecode(parts[1]);
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("sub", out var subEl)
            || subEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("JWT missing 'sub' claim");

        return subEl.GetString()!;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var normalized = input.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
            case 1: throw new FormatException("Malformed base64url");
        }
        return Convert.FromBase64String(normalized);
    }
}
