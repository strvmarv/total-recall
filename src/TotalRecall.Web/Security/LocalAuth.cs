using System;
using System.Security.Cryptography;

namespace TotalRecall.Web.Security;

/// <summary>
/// Stateless helpers for the local-only auth model: an ephemeral per-launch
/// bearer token injected into index.html and required on /api/*, plus a
/// Host-header allowlist that mitigates DNS-rebinding against the loopback
/// server. No multi-user auth; single local user.
/// </summary>
public static class LocalAuth
{
    /// <summary>Cryptographically random URL-safe token (24 bytes -> 32 chars).</summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// True when the request Host header's hostname is loopback (localhost / 127.0.0.1
    /// / ::1) OR matches <paramref name="allowedHost"/>. By contract,
    /// <paramref name="allowedHost"/> is the server's actual bound host (the caller
    /// passes the address Kestrel is listening on). When the operator opts into a
    /// non-loopback bind via `--host`, that host becomes allowed by design — the
    /// CLI warns about the exposure at launch. Loopback is always allowed.
    /// </summary>
    public static bool IsAllowedHost(string? hostHeader, string allowedHost)
    {
        if (string.IsNullOrEmpty(hostHeader)) return false;
        var host = StripPort(hostHeader);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host is "127.0.0.1" or "::1") return true;
        return host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Constant-time token comparison. Both inputs are hashed to a fixed 32-byte
    /// SHA-256 digest before comparison, so the comparison is constant-time
    /// regardless of input length (FixedTimeEquals itself requires equal-length spans).
    /// </summary>
    public static bool TokenMatches(string expected, string? provided)
    {
        if (provided is null) return false;
        Span<byte> a = stackalloc byte[32];
        Span<byte> b = stackalloc byte[32];
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected), a);
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string StripPort(string hostHeader)
    {
        // IPv6 with brackets: [::1]:5577 -> ::1
        if (hostHeader.StartsWith('['))
        {
            var close = hostHeader.IndexOf(']');
            return close > 0 ? hostHeader.Substring(1, close - 1) : hostHeader;
        }
        // Bare IPv6 (multiple colons, no brackets, no port): return as-is so
        // "::1" is recognized as loopback rather than misparsed at the first colon.
        if (hostHeader.IndexOf(':') != hostHeader.LastIndexOf(':'))
            return hostHeader;
        // IPv4 / hostname with optional :port
        var colon = hostHeader.IndexOf(':');
        return colon >= 0 ? hostHeader.Substring(0, colon) : hostHeader;
    }
}
