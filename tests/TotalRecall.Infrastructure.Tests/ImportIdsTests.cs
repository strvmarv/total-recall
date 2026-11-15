using System;
using System.Text.RegularExpressions;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Git sync milestone 0a: the hash-derived id that a memory import gets, so
/// two machines importing the same host memory write the same entry id.
/// </summary>
public class ImportIdsTests
{
    private static readonly Regex IdRule = new("^[0-9a-f][0-9a-f-]{7,63}$");

    [Fact]
    public void FromHash_KnownValue()
    {
        // First 16 bytes of SHA-256("abc") = ba7816bf 8f01cfea 414140de 5dae2223.
        Assert.Equal("ba7816bf-8f01-cfea-4141-40de5dae2223", ImportIds.FromHash("abc"));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("Line one\r\nLine two\n")]
    [InlineData("unicode: héllo — ✓")]
    public void FromHash_MatchesIdRule_ForRealImportHashes(string raw)
    {
        var id = ImportIds.FromHash(ImportLog.ContentHash(raw));

        Assert.Equal(36, id.Length);
        Assert.Matches(IdRule, id);
    }

    [Fact]
    public void FromHash_IsDeterministic_AndDiffersByHash()
    {
        var a1 = ImportIds.FromHash("hash-a");
        var a2 = ImportIds.FromHash("hash-a");
        var b = ImportIds.FromHash("hash-b");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void FromHash_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ImportIds.FromHash(null!));
    }
}
