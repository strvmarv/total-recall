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
    /// True when the request Host header's hostname is loopback OR matches the
    /// server's explicitly-bound host. Strips any port and IPv6 brackets.
    /// </summary>
    public static bool IsAllowedHost(string? hostHeader, string allowedHost)
    {
        if (string.IsNullOrEmpty(hostHeader)) return false;
        var host = StripPort(hostHeader);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host is "127.0.0.1" or "::1") return true;
        return host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Constant-time token comparison.</summary>
    public static bool TokenMatches(string expected, string? provided)
    {
        if (provided is null) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string StripPort(string hostHeader)
    {
        // IPv6: [::1]:5577 -> ::1
        if (hostHeader.StartsWith('['))
        {
            var close = hostHeader.IndexOf(']');
            return close > 0 ? hostHeader.Substring(1, close - 1) : hostHeader;
        }
        var colon = hostHeader.IndexOf(':');
        return colon >= 0 ? hostHeader.Substring(0, colon) : hostHeader;
    }
}
