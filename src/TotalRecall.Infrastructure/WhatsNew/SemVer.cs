// src/TotalRecall.Infrastructure/WhatsNew/SemVer.cs
//
// Pure SemVer 2.0.0 parsing and precedence for the what's-new notice. No I/O.
// Numeric parts are kept as digit strings so arbitrarily long values compare
// exactly (length, then ordinal) without overflow.

using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.WhatsNew;

/// <summary>A parsed version: digit strings and prerelease identifiers. Build metadata is dropped.</summary>
public readonly record struct ParsedVersion(string Major, string Minor, string Patch, string[] Prerelease);

public static class SemVer
{
    /// <summary>
    /// Parses <c>MAJOR.MINOR.PATCH[-pre][+build]</c>. A leading <c>v</c>, leading zeros in a
    /// numeric part, empty identifiers and non-ASCII characters are rejected. The
    /// <c>+build</c> suffix is validated as identifiers and then dropped.
    /// </summary>
    public static bool TryParse(string? text, out ParsedVersion v)
    {
        v = default;
        if (string.IsNullOrEmpty(text)) return false;

        var s = text.AsSpan();
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            if (!AreIdentifiers(s[(plus + 1)..].ToString(), numericNoLeadingZero: false)) return false;
            s = s[..plus];
        }

        string[] pre = Array.Empty<string>();
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var preText = s[(dash + 1)..].ToString();
            if (!AreIdentifiers(preText, numericNoLeadingZero: true)) return false;
            pre = preText.Split('.');
            s = s[..dash];
        }

        var core = s.ToString().Split('.');
        if (core.Length != 3) return false;
        foreach (var part in core)
        {
            if (!IsNumeric(part)) return false;
        }

        v = new ParsedVersion(core[0], core[1], core[2], pre);
        return true;
    }

    /// <summary>SemVer precedence: negative if a &lt; b, zero if equal, positive if a &gt; b.</summary>
    public static int Compare(ParsedVersion a, ParsedVersion b)
    {
        var c = CompareNumeric(a.Major, b.Major);
        if (c != 0) return c;
        c = CompareNumeric(a.Minor, b.Minor);
        if (c != 0) return c;
        c = CompareNumeric(a.Patch, b.Patch);
        if (c != 0) return c;

        var pa = a.Prerelease ?? Array.Empty<string>();
        var pb = b.Prerelease ?? Array.Empty<string>();
        // A release ranks above its prereleases.
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;

        var n = Math.Min(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var ia = pa[i];
            var ib = pb[i];
            var na = IsNumeric(ia);
            var nb = IsNumeric(ib);
            if (na && nb)
            {
                c = CompareNumeric(ia, ib);
            }
            else if (na)
            {
                c = -1; // numeric identifiers rank below alphanumeric ones
            }
            else if (nb)
            {
                c = 1;
            }
            else
            {
                c = Math.Sign(string.CompareOrdinal(ia, ib));
            }
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary>True for null/blank/<c>unknown</c>/<c>1.0.0</c>/unparseable: a build with no real version.</summary>
    public static bool IsDevBuild(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return true;
        if (!TryParse(version, out var v)) return true;
        return v.Major == "1" && v.Minor == "0" && v.Patch == "0" && v.Prerelease.Length == 0;
    }

    private static int CompareNumeric(string a, string b)
    {
        if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    // Digits only (ASCII), no leading zero unless the value is exactly "0".
    private static bool IsNumeric(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }
        return s.Length == 1 || s[0] != '0';
    }

    private static bool AreIdentifiers(string text, bool numericNoLeadingZero)
    {
        if (text.Length == 0) return false;
        foreach (var id in text.Split('.'))
        {
            if (id.Length == 0) return false;
            var allDigits = true;
            foreach (var ch in id)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-')) return false;
                if (!char.IsAsciiDigit(ch)) allDigits = false;
            }
            if (numericNoLeadingZero && allDigits && id.Length > 1 && id[0] == '0') return false;
        }
        return true;
    }
}
