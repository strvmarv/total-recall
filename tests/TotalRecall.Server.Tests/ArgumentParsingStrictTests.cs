// tests/TotalRecall.Server.Tests/ArgumentParsingStrictTests.cs
//
// The strict integer readers (a fractional number is refused rather than truncated) and the shared bool reader used by
// the transcript tools.

namespace TotalRecall.Server.Tests;

using System;
using System.Text.Json;
using TotalRecall.Server.Handlers;
using Xunit;

public sealed class ArgumentParsingStrictTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("""{"n":null}""", null)]
    [InlineData("""{"n":2}""", 2)]
    [InlineData("""{"n":2.0}""", 2)]
    public void StrictInt_Absent_Null_Whole_AreAccepted(string json, int? expected)
    {
        Assert.Equal(expected, ArgumentParsing.ReadOptionalStrictInt(Parse(json), "n", 0, 10));
    }

    [Theory]
    [InlineData("""{"n":1.5}""")]
    [InlineData("""{"n":200.9}""")]
    public void StrictInt_Fraction_Refused_NamingTheArgument(string json)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt(Parse(json), "n", 0, 1000));
        Assert.Contains("n must be a whole number", ex.Message);
    }

    [Theory]
    [InlineData("""{"n":"3"}""")]
    [InlineData("""{"n":true}""")]
    [InlineData("""{"n":-1}""")]
    [InlineData("""{"n":11}""")]
    [InlineData("""{"n":[1]}""")]
    public void StrictInt_OutOfRange_And_NonNumber_Refused(string json)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt(Parse(json), "n", 0, 10));
        Assert.Contains("n must be", ex.Message);
    }

    [Fact]
    public void StrictInt_OutOfRange_MessageNamesTheRange()
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt(Parse("""{"n":11}"""), "n", 0, 10));
        Assert.Equal("n must be between 0 and 10", ex.Message);
    }

    [Fact]
    public void StrictInt_HugeNumber_IsOutOfRange_NotAnOverflow()
    {
        Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt(Parse("""{"n":1e300}"""), "n", 0, 10));
        Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt(Parse("""{"n":99999999999999999999}"""), "n", 0, 10));
    }

    [Fact]
    public void StrictInt64_AcceptsAbove2To31()
    {
        Assert.Equal(3000000000L, ArgumentParsing.ReadOptionalStrictInt64(Parse("""{"n":3000000000}"""), "n", 1, long.MaxValue));
        Assert.Null(ArgumentParsing.ReadOptionalStrictInt64(Parse("{}"), "n", 1, long.MaxValue));
        Assert.Null(ArgumentParsing.ReadOptionalStrictInt64(Parse("""{"n":null}"""), "n", 1, long.MaxValue));
        Assert.Equal(5L, ArgumentParsing.ReadOptionalStrictInt64(Parse("""{"n":5.0}"""), "n", 1, long.MaxValue));
    }

    [Fact]
    public void StrictInt64_Fraction_And_OutOfRange_And_NonNumber_Refused()
    {
        var f = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt64(Parse("""{"n":1.5}"""), "n", 1, 100));
        Assert.Contains("n must be a whole number", f.Message);
        var r = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt64(Parse("""{"n":0}"""), "n", 1, 100));
        Assert.Equal("n must be between 1 and 100", r.Message);
        var s = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalStrictInt64(Parse("""{"n":"7"}"""), "n", 1, 100));
        Assert.Contains("n must be", s.Message);
    }

    [Fact]
    public void ReadOptionalBool_TrueFalse()
    {
        Assert.True(ArgumentParsing.ReadOptionalBool(Parse("""{"b":true}"""), "b"));
        Assert.False(ArgumentParsing.ReadOptionalBool(Parse("""{"b":false}"""), "b"));
    }

    [Fact]
    public void ReadOptionalBool_Null_And_Absent_GiveNull()
    {
        Assert.Null(ArgumentParsing.ReadOptionalBool(Parse("""{"b":null}"""), "b"));
        Assert.Null(ArgumentParsing.ReadOptionalBool(Parse("{}"), "b"));
    }

    [Fact]
    public void ReadOptionalBool_NonBoolean_ThrowsNamingTheArgument()
    {
        var ex = Assert.Throws<ArgumentException>(() => ArgumentParsing.ReadOptionalBool(Parse("""{"b":"yes"}"""), "b"));
        Assert.Equal("b must be a boolean", ex.Message);
    }
}
