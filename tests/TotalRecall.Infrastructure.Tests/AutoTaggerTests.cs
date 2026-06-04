using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

public sealed class AutoTaggerTests
{
    [Fact]
    public void ExtractKeywords_ReturnsTopKeywords_ByFrequency()
    {
        var result = AutoTagger.ExtractKeywords(
            "Use parameterized queries instead of string concatenation in SQL queries");

        Assert.NotEmpty(result);
        Assert.True(result.Length <= 3);
        // "queries" appears twice, so it should be the top keyword
        Assert.Contains("queries", result);
    }

    [Fact]
    public void ExtractKeywords_ExcludesStopWords()
    {
        var result = AutoTagger.ExtractKeywords(
            "the a an is are and of in to for it on at with");

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractKeywords_ShortWords_FilteredOut()
    {
        // "or" is 2 chars (below minLength=3), "not" is a stop word,
        // all other words ("a", "is", "to", "be") are stop words
        var result = AutoTagger.ExtractKeywords("a is to be or not to be", minLength: 3);
        Assert.DoesNotContain("to", result);
        Assert.DoesNotContain("be", result);
        Assert.DoesNotContain("or", result); // too short
    }

    [Fact]
    public void ExtractKeywords_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(AutoTagger.ExtractKeywords(""));
        Assert.Empty(AutoTagger.ExtractKeywords("   "));
        Assert.Empty(AutoTagger.ExtractKeywords(null!));
    }

    [Fact]
    public void ExtractKeywords_LessThanMax_ReturnsWhatItHas()
    {
        var result = AutoTagger.ExtractKeywords("hello world", maxKeywords: 5);
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void FormatCompactTags_WithUserTags_UsesThemOverAutoExtraction()
    {
        var result = AutoTagger.FormatCompactTags(
            EntryType.Correction,
            new[] { "sql", "security" },
            "Use parameterized queries instead of string concatenation");

        Assert.Contains("correction", result);
        Assert.Contains("sql", result);
        Assert.Contains("security", result);
    }

    [Fact]
    public void FormatCompactTags_WithoutTags_ExtractsFromContent()
    {
        var result = AutoTagger.FormatCompactTags(
            EntryType.Preference,
            null,
            "Use spaces not tabs for indentation");

        Assert.Contains("preference", result);
        Assert.Contains("spaces", result);
    }

    [Fact]
    public void FormatCompactTags_TruncatesLongTagLists()
    {
        var result = AutoTagger.FormatCompactTags(
            EntryType.Decision,
            new[] { "this-is-a-very-long-tag-name", "another-very-long-tag", "yet-another-one", "fourth", "fifth" },
            null,
            maxChars: 50);

        Assert.True(result.Length <= 52, $"result '{result}' was too long ({result.Length})");
        Assert.StartsWith("[decision", result);
        Assert.EndsWith("]", result);
    }

    [Fact]
    public void FormatCompactTags_EntryTypeOnly_WhenNoContent()
    {
        var result = AutoTagger.FormatCompactTags(
            EntryType.Imported, null, null);

        Assert.Equal("[imported]", result);
    }

    [Fact]
    public void FormatCompactTags_EntryTypeOnly_WhenEmptyContent()
    {
        var result = AutoTagger.FormatCompactTags(
            EntryType.Compacted, null, "");

        Assert.Equal("[compacted]", result);
    }

    [Fact]
    public void FormatCompactTags_DifferentiatesAllEntryTypes()
    {
        var types = new[]
        {
            EntryType.Correction, EntryType.Preference, EntryType.Decision,
            EntryType.Surfaced, EntryType.Imported, EntryType.Compacted,
            EntryType.Ingested,
        };

        foreach (var t in types)
        {
            var result = AutoTagger.FormatCompactTags(t, null, "test content");
            Assert.NotEmpty(result);
            Assert.StartsWith("[", result);
            Assert.EndsWith("]", result);
        }
    }

    [Fact]
    public void FormatCompactTags_AOT_Safe_NoToStringCalls()
    {
        // Verify EntryTypeToLower is explicit (no .ToString())
        var result = AutoTagger.FormatCompactTags(
            EntryType.Correction, null, "a b c d e f g");

        Assert.Contains("correction", result);
        Assert.DoesNotContain("Correction", result);
    }
}
