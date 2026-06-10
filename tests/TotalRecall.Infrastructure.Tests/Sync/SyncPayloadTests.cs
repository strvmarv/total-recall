using System;
using System.Text.Json;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

/// <summary>
/// Pins <see cref="SyncPayload"/> JSON correctness. The hand-rolled (AOT-safe)
/// builder must emit RFC 8259-compliant strings — in particular it must escape
/// ALL C0 control characters, not just \n \r \t. A raw control byte in memory
/// content (e.g. a code snippet containing U+0001) previously produced invalid
/// JSON that wedged the entire sync queue: every flush re-threw a JsonException
/// on the un-parseable payload before any item could be marked completed/failed.
/// </summary>
public sealed class SyncPayloadTests
{
    private static Entry MakeEntry(string content) =>
        new(
            "id-1", content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            0L, 0L, 0L, 0, 1.0,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}", 0);

    [Fact]
    public void Upsert_ContentWithSohControlChar_ProducesParseableJsonThatRoundTrips()
    {
        // The real-world poison pill: a captured code snippet containing a raw
        // U+0001 (SOH) byte, e.g. string.Join("<SOH>", ...).
        var content = "string.Join(\"" + (char)0x01 + "\", new[] { a, b })";
        var payload = SyncPayload.Upsert(MakeEntry(content), ContentType.Memory, Tier.Hot);

        // Must be valid JSON (this is exactly what FlushAsync's JsonDocument.Parse does).
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(content, doc.RootElement.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x07)]
    [InlineData(0x08)] // backspace
    [InlineData(0x0B)] // vertical tab
    [InlineData(0x0C)] // form feed
    [InlineData(0x1F)]
    public void Upsert_AllC0ControlChars_AreEscapedToValidJson(int controlCodepoint)
    {
        var content = "before" + (char)controlCodepoint + "after";
        var payload = SyncPayload.Upsert(MakeEntry(content), ContentType.Memory, Tier.Hot);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(content, doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Upsert_OrdinaryContent_StillRoundTripsUnchanged()
    {
        var content = "normal content with \"quotes\", \\backslash\\, and\ttabs\nnewlines";
        var payload = SyncPayload.Upsert(MakeEntry(content), ContentType.Memory, Tier.Hot);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(content, doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Upsert_ControlCharsInTagsAndSource_AreEscaped()
    {
        // Escape() is also applied to tags, source, and scope — not just content.
        var entry = new Entry(
            "id-1", "content",
            FSharpOption<string>.None,
            FSharpOption<string>.Some("src" + (char)0x02 + "tool"),
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(new[] { "ta" + (char)0x01 + "g" }),
            0L, 0L, 0L, 0, 1.0,
            FSharpOption<string>.None, FSharpOption<string>.None,
            "sco" + (char)0x1F + "pe", EntryType.Preference, "{}", 0);

        var payload = SyncPayload.Upsert(entry, ContentType.Memory, Tier.Hot);

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Assert.Equal("src" + (char)0x02 + "tool", root.GetProperty("source").GetString());
        Assert.Equal("ta" + (char)0x01 + "g", root.GetProperty("tags")[0].GetString());
        Assert.Equal("sco" + (char)0x1F + "pe", root.GetProperty("scope").GetString());
    }
}
