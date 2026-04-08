using TotalRecall.Infrastructure.Search;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Pure-helper unit tests for <see cref="FtsSearch.SanitizeFtsQuery"/>.
/// These do not touch the database, so they are not tagged Integration.
/// </summary>
public sealed class SanitizeFtsQueryTests
{
    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", FtsSearch.SanitizeFtsQuery(""));
    }

    [Fact]
    public void WhitespaceOnly_ReturnsEmpty()
    {
        Assert.Equal("", FtsSearch.SanitizeFtsQuery("   \t \n "));
    }

    [Fact]
    public void SingleWord_IsQuoted()
    {
        Assert.Equal("\"hello\"", FtsSearch.SanitizeFtsQuery("hello"));
    }

    [Fact]
    public void MultipleWords_EachQuotedSpaceJoined()
    {
        Assert.Equal(
            "\"hello\" \"world\"",
            FtsSearch.SanitizeFtsQuery("hello world"));
    }

    [Fact]
    public void CollapsesRunsOfWhitespace()
    {
        Assert.Equal(
            "\"hello\" \"world\"",
            FtsSearch.SanitizeFtsQuery("  hello   world  "));
    }

    [Fact]
    public void InternalDoubleQuotes_AreDoubledAndWrapped()
    {
        // hello"world -> wrapped as "hello""world"
        Assert.Equal(
            "\"hello\"\"world\"",
            FtsSearch.SanitizeFtsQuery("hello\"world"));
    }

    [Fact]
    public void LeadingDash_IsQuotedVerbatim()
    {
        // -baz -> "-baz" (would otherwise be FTS5 NOT-operator)
        Assert.Equal("\"-baz\"", FtsSearch.SanitizeFtsQuery("-baz"));
    }

    [Fact]
    public void MixedExample_MatchesTsReference()
    {
        // TS reference doc example:
        //   input:  hello "world -baz
        //   output: "hello" """world" "-baz"
        //
        // Explanation: the second token is the literal word `"world` (a
        // single double-quote followed by `world`). That word's internal
        // quote is doubled to `""world`, then wrapped: `"""world"`.
        Assert.Equal(
            "\"hello\" \"\"\"world\" \"-baz\"",
            FtsSearch.SanitizeFtsQuery("hello \"world -baz"));
    }
}
