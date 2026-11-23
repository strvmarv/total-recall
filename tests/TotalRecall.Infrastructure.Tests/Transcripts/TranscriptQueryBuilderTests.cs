using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class TranscriptQueryBuilderTests : IDisposable
{
    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Phrase_ErrorString_KeepsTokenSequence() =>
        Assert.Equal("\"sqlite busy database is locked\"", TranscriptQueryBuilder.Phrase("SQLITE_BUSY: database is locked"));

    [Theory]
    [InlineData("")]
    [InlineData("single")]
    [InlineData("  ---  ")]
    [InlineData("\"")]
    public void Phrase_OneToken_IsNull(string query) => Assert.Null(TranscriptQueryBuilder.Phrase(query));

    [Fact]
    public void Words_SentenceBecomesOrOfQuotedTokens() =>
        Assert.Equal("\"how\" OR \"do\" OR \"we\" OR \"ship\"", TranscriptQueryBuilder.Words("How do we ship?"));

    [Fact]
    public void Words_DropsOneCharTokens() =>
        Assert.Equal("\"ship\" OR \"it\"", TranscriptQueryBuilder.Words("a ship b it c"));

    [Fact]
    public void Words_OnlyOneCharTokens_IsNull() => Assert.Null(TranscriptQueryBuilder.Words("a b c"));

    [Fact]
    public void Words_CapsAtTwelveLongest_KeepsFirstPositionOrder()
    {
        // 14 tokens: two of length 2 (the shortest) must go; among equal lengths, the earlier position wins.
        var query = "aa bbb ccc ddd eee fff ggg hhh iii jjj kkk lll mmm zz";
        var words = TranscriptQueryBuilder.Words(query)!;
        var parts = words.Split(" OR ");
        Assert.Equal(12, parts.Length);
        Assert.DoesNotContain("\"aa\"", parts);
        Assert.DoesNotContain("\"zz\"", parts);
        Assert.Equal(new[] { "bbb", "ccc", "ddd", "eee", "fff", "ggg", "hhh", "iii", "jjj", "kkk", "lll", "mmm" }
            .Select(t => "\"" + t + "\""), parts);
    }

    [Fact]
    public void Words_LongerLaterTokensDisplaceEarlierShorterOnes()
    {
        var query = "ab cd ef gh ij kl mn op qr st uv wx longer1 longer2";
        var parts = TranscriptQueryBuilder.Words(query)!.Split(" OR ");
        Assert.Equal(12, parts.Length);
        Assert.Equal("\"longer1\"", parts[^2]);
        Assert.Equal("\"longer2\"", parts[^1]);
        Assert.Equal("\"ab\"", parts[0]); // ties among the 2-character tokens go to the first positions
        Assert.DoesNotContain("\"wx\"", parts);
    }

    [Fact]
    public void Words_RepeatedTokenCountsOnce() =>
        Assert.Equal("\"lock\" OR \"timeout\"", TranscriptQueryBuilder.Words("lock timeout lock LOCK"));

    [Theory]
    [InlineData("-foo")]
    [InlineData("foo*")]
    [InlineData("a:b")]
    [InlineData("NEAR(a b)")]
    [InlineData("a OR b")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("\"unbalanced")]
    [InlineData("... --- !!!")]
    [InlineData("über Straße")]
    [InlineData("日本語のテキスト 検索")]
    [InlineData("foo AND NOT bar ^baz {x y} (z)")]
    [InlineData("col:value title: \"x y\" + - * ~")]
    public void OddInputs_NeverProduceTextFtsRejects(string query)
    {
        foreach (var match in new[] { TranscriptQueryBuilder.Phrase(query), TranscriptQueryBuilder.Words(query) })
        {
            if (match is not null)
                Execute(match);
        }
    }

    [Fact]
    public void OnlyPunctuation_IsNull()
    {
        Assert.Null(TranscriptQueryBuilder.Phrase("... --- !!!"));
        Assert.Null(TranscriptQueryBuilder.Words("... --- !!!"));
    }

    [Fact]
    public void TenThousandCharacterQuery_Executes()
    {
        var query = string.Join(' ', Enumerable.Range(0, 1700).Select(i => "tok" + i));
        Assert.True(query.Length > 10_000);
        var phrase = TranscriptQueryBuilder.Phrase(query);
        var words = TranscriptQueryBuilder.Words(query);
        Assert.NotNull(phrase);
        Assert.NotNull(words);
        Assert.Equal(12, words!.Split(" OR ").Length);
        Execute(phrase!);
        Execute(words);
    }

    [Fact]
    public void NonAscii_IsLowerCasedAndKept()
    {
        Assert.Equal("\"über\" OR \"straße\"", TranscriptQueryBuilder.Words("Über STRAßE"));
        Assert.Equal("\"über straße\"", TranscriptQueryBuilder.Phrase("über, Straße"));
    }

    // These tests use no normalisation, so they hold with ICU and in invariant globalization mode (the shipped
    // binaries set InvariantGlobalization, where string.Normalize does nothing).
    [Fact]
    public void DecomposedQuery_StaysOneToken()
    {
        // "re\u0301sume\u0301" is e + COMBINING ACUTE ACCENT: the mark is kept inside the token, not a separator.
        Assert.Equal("\"re\u0301sume\u0301\"", TranscriptQueryBuilder.Words("Re\u0301sume\u0301"));
        Assert.Equal("\"my re\u0301sume\u0301\"", TranscriptQueryBuilder.Phrase("my re\u0301sume\u0301"));
    }

    [Theory]
    [InlineData("a\u0903b")] // DEVANAGARI SIGN VISARGA, a spacing combining mark (Mc)
    [InlineData("a\u20DDb")] // COMBINING ENCLOSING CIRCLE, an enclosing mark (Me)
    [InlineData("a\u0301b")] // COMBINING ACUTE ACCENT, a non-spacing mark (Mn)
    public void CombiningMarks_AreTokenCharacters(string word)
    {
        Assert.Equal("\"" + word + "\"", TranscriptQueryBuilder.Words(word));
        Assert.Null(TranscriptQueryBuilder.Phrase(word));
    }

    [Fact]
    public void LoneCombiningMark_AfterASeparator_IsNoToken() =>
        Assert.Equal("\"ab\" OR \"cd\"", TranscriptQueryBuilder.Words("ab \u0301 cd"));

    [Fact]
    public void LoneSurrogate_DoesNotThrow()
    {
        Assert.Equal("\"abc\" OR \"def\"", TranscriptQueryBuilder.Words("abc\uD800def"));
        Assert.Equal("\"abc def\"", TranscriptQueryBuilder.Phrase("abc\uD800def"));
    }

    [Fact]
    public void DecomposedQuery_MatchesTheIndexedWord()
    {
        var sid = _db.AddSession("pi", "s1");
        var hit = _db.AddChunk(sid, "please update the resume template", 0);
        _db.AddChunk(sid, "nothing relevant here", 1);

        Assert.Equal(new[] { hit }, MatchIds(TranscriptQueryBuilder.Words("re\u0301sume\u0301")!));
        Assert.Equal(new[] { hit }, MatchIds(TranscriptQueryBuilder.Phrase("the re\u0301sume\u0301")!));
        // The precomposed form folds the same way in the unicode61 tokenizer.
        Assert.Equal(new[] { hit }, MatchIds(TranscriptQueryBuilder.Words("r\u00e9sum\u00e9")!));
    }

    [Fact]
    public void Phrase_MatchesTheTextAsTheTokenizerSplitsIt()
    {
        var sid = _db.AddSession("pi", "s1");
        var hit = _db.AddChunk(sid, "we saw SQLITE_BUSY: database is locked during ingest", 0);
        _db.AddChunk(sid, "the database was busy and then is locked later", 1);

        Assert.Equal(new[] { hit }, MatchIds(TranscriptQueryBuilder.Phrase("SQLITE_BUSY: database is locked")!));
    }

    private void Execute(string match) => MatchIds(match);

    private long[] MatchIds(string match)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM tx_chunk_fts WHERE tx_chunk_fts MATCH $m";
        cmd.Parameters.AddWithValue("$m", match);
        var ids = new System.Collections.Generic.List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids.ToArray();
    }
}
