// tests/TotalRecall.Server.Tests/ArgumentParsingTests.cs
//
// Unit tests for ArgumentParsing.ReadTags — verifies that tags are accepted
// as native arrays, JSON-encoded array strings, and comma-separated strings.

namespace TotalRecall.Server.Tests;

using System;
using System.Text.Json;
using TotalRecall.Server.Handlers;
using Xunit;

public sealed class ArgumentParsingTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ReadTags_NativeArray_ReturnsStrings()
    {
        var args = Parse("""{"tags":["git","commits","preference"]}""");
        var result = ArgumentParsing.ReadTags(args);
        Assert.NotNull(result);
        Assert.Equal(new[] { "git", "commits", "preference" }, result);
    }

    [Fact]
    public void ReadTags_JsonEncodedArrayString_ParsesCorrectly()
    {
        // This is what Claude Code sends: a JSON array serialized as a string value.
        var args = Parse("""{"tags":"[\"git\",\"commits\",\"preference\"]"}""");
        var result = ArgumentParsing.ReadTags(args);
        Assert.NotNull(result);
        Assert.Equal(new[] { "git", "commits", "preference" }, result);
    }

    [Fact]
    public void ReadTags_CommaSeparatedString_SplitsAndTrims()
    {
        var args = Parse("""{"tags":"git, commits, preference"}""");
        var result = ArgumentParsing.ReadTags(args);
        Assert.NotNull(result);
        Assert.Equal(new[] { "git", "commits", "preference" }, result);
    }

    [Fact]
    public void ReadTags_SingleString_ReturnsSingleElement()
    {
        var args = Parse("""{"tags":"git"}""");
        var result = ArgumentParsing.ReadTags(args);
        Assert.NotNull(result);
        Assert.Equal(new[] { "git" }, result);
    }

    [Fact]
    public void ReadTags_Missing_ReturnsNull()
    {
        var args = Parse("""{"content":"hello"}""");
        Assert.Null(ArgumentParsing.ReadTags(args));
    }

    [Fact]
    public void ReadTags_Null_ReturnsNull()
    {
        var args = Parse("""{"tags":null}""");
        Assert.Null(ArgumentParsing.ReadTags(args));
    }

    [Fact]
    public void ReadTags_EmptyString_ReturnsNull()
    {
        var args = Parse("""{"tags":""}""");
        Assert.Null(ArgumentParsing.ReadTags(args));
    }

    [Fact]
    public void ReadTags_EmptyArray_ReturnsEmpty()
    {
        var args = Parse("""{"tags":[]}""");
        var result = ArgumentParsing.ReadTags(args);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadTags_NumberInArray_Throws()
    {
        var args = Parse("""{"tags":["ok", 42]}""");
        var ex = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadTags(args));
        Assert.Contains("tags[1] must be a string", ex.Message);
    }

    [Fact]
    public void ReadTags_NumericValue_Throws()
    {
        var args = Parse("""{"tags":123}""");
        Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadTags(args));
    }
}
