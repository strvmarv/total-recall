using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class ClaudeRecordMapperTests
{
    private const string Ts = "\"timestamp\":\"2026-01-01T00:00:05.000Z\"";

    private static IReadOnlyList<TranscriptRecord> Map(string json, long start = 10, long end = 99)
    {
        using var doc = JsonDocument.Parse(json);
        return ClaudeRecordMapper.Map(doc.RootElement, start, end);
    }

    private static string User(string content, string extra = "") =>
        "{\"type\":\"user\"," + Ts + ",\"message\":{\"role\":\"user\",\"content\":" + content + "}" + extra + "}";

    private static string Assistant(string blocks, string extra = "") =>
        "{\"type\":\"assistant\"," + Ts + ",\"message\":{\"role\":\"assistant\",\"content\":[" + blocks + "]}" + extra + "}";

    private static string ToolUse(string name, string input) =>
        "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"" + name + "\",\"input\":" + input + "}";

    private static TranscriptRecord Single(IReadOnlyList<TranscriptRecord> records)
    {
        Assert.Single(records);
        return records[0];
    }

    [Fact]
    public void UserString_IsUserText()
    {
        var r = Single(Map(User("\"Please fix the build\"")));
        Assert.Equal(RecordKind.UserText, r.Kind);
        Assert.Equal("Please fix the build", r.Text);
        Assert.Null(r.ToolName);
    }

    [Fact]
    public void UserTextBlocks_JoinedWithBlankLine()
    {
        var r = Single(Map(User("[{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"image\",\"source\":{}},{\"type\":\"text\",\"text\":\"second\"}]")));
        Assert.Equal(RecordKind.UserText, r.Kind);
        Assert.Equal("first\n\nsecond", r.Text);
    }

    [Fact]
    public void UserOriginHuman_IsUserText()
    {
        var r = Single(Map(User("\"hello\"", ",\"origin\":{\"kind\":\"human\"}")));
        Assert.Equal(RecordKind.UserText, r.Kind);
    }

    [Fact]
    public void UserOriginTaskNotification_IsNothing()
    {
        Assert.Empty(Map(User("\"<task-notification>done</task-notification> real words\"", ",\"origin\":{\"kind\":\"task-notification\"}")));
        Assert.Empty(Map(User("\"words\"", ",\"origin\":{\"kind\":5}")));
        // a failed tool result in a host-injected record is host output too
        Assert.Empty(Map(User("[{\"type\":\"tool_result\",\"is_error\":true,\"content\":\"boom\"}]", ",\"origin\":{\"kind\":\"other\"}")));
    }

    private static string Queued(string attachment) =>
        "{\"type\":\"attachment\"," + Ts + ",\"attachment\":" + attachment + "}";

    [Fact]
    public void QueuedHumanPromptAttachment_IsUserTextOfPrompt()
    {
        var r = Single(Map(Queued("{\"type\":\"queued_command\",\"prompt\":\"also check the index\",\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"human\"}}")));
        Assert.Equal(RecordKind.UserText, r.Kind);
        Assert.Equal("also check the index", r.Text);
        Assert.Equal(10, r.StartOffset);
        Assert.Equal(99, r.EndOffset);
    }

    [Theory]
    [InlineData("{\"type\":\"queued_command\",\"prompt\":\"p\",\"commandMode\":\"bash\",\"origin\":{\"kind\":\"human\"}}")]
    [InlineData("{\"type\":\"queued_command\",\"prompt\":\"p\",\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"task-notification\"}}")]
    [InlineData("{\"type\":\"queued_command\",\"prompt\":\"p\",\"commandMode\":\"prompt\"}")]
    [InlineData("{\"type\":\"queued_command\",\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"human\"}}")]
    [InlineData("{\"type\":\"queued_command\",\"prompt\":5,\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"human\"}}")]
    [InlineData("{\"type\":\"edited_text_file\",\"prompt\":\"p\",\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"human\"}}")]
    [InlineData("\"not an object\"")]
    public void QueuedAttachmentNotHumanOrNotPrompt_IsNothing(string attachment)
    {
        Assert.Empty(Map(Queued(attachment)));
    }

    [Fact]
    public void QueuedAttachmentWithoutAttachmentObject_IsNothing()
    {
        Assert.Empty(Map("{\"type\":\"attachment\"}"));
    }

    [Fact]
    public void ToolResultError_StringAndTextBlocks_IsToolError()
    {
        var s = Single(Map(User("[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"is_error\":true,\"content\":\"Exit code 1\\nboom\"}]")));
        Assert.Equal(RecordKind.ToolError, s.Kind);
        Assert.Equal("Exit code 1\nboom", s.Text);

        var b = Single(Map(User("[{\"type\":\"tool_result\",\"is_error\":true,\"content\":[{\"type\":\"text\",\"text\":\"one\"},{\"type\":\"image\"},{\"type\":\"text\",\"text\":\"two\"}]}]")));
        Assert.Equal(RecordKind.ToolError, b.Kind);
        Assert.Equal("onetwo", b.Text);
    }

    [Fact]
    public void ToolResultError_WithoutText_IsToolErrorWithEmptyText()
    {
        var r = Single(Map(User("[{\"type\":\"tool_result\",\"is_error\":true}]")));
        Assert.Equal(RecordKind.ToolError, r.Kind);
        Assert.Equal("", r.Text);
    }

    [Fact]
    public void ToolResultSuccess_IsNothing()
    {
        Assert.Empty(Map(User("[{\"type\":\"tool_result\",\"content\":\"zqxtoolresult\"}]")));
        Assert.Empty(Map(User("[{\"type\":\"tool_result\",\"is_error\":false,\"content\":\"zqxtoolresult\"}]")));
        Assert.Empty(Map(User("[{\"type\":\"tool_result\",\"is_error\":\"true\",\"content\":\"zqxtoolresult\"}]")));
    }

    [Fact]
    public void UserWithTextAndError_TextFirstThenError()
    {
        var records = Map(User("[{\"type\":\"tool_result\",\"is_error\":true,\"content\":\"bad\"},{\"type\":\"text\",\"text\":\"and then\"}]"));
        Assert.Equal(new[] { RecordKind.UserText, RecordKind.ToolError }, records.Select(r => r.Kind));
        Assert.Equal("and then", records[0].Text);
        Assert.Equal("bad", records[1].Text);
    }

    [Fact]
    public void UserTextWithShellInput_UserTextThenBashStub_ThenErrors()
    {
        var records = Map(User("[{\"type\":\"text\",\"text\":\"look <bash-input>ls -l</bash-input>\"},{\"type\":\"tool_result\",\"is_error\":true,\"content\":\"bad\"}]"));
        Assert.Equal(new[] { RecordKind.UserText, RecordKind.ToolStub, RecordKind.ToolError }, records.Select(r => r.Kind));
        Assert.Equal("look", records[0].Text);
        Assert.Equal("Bash", records[1].ToolName);
        Assert.Equal("ls -l", records[1].Text);
    }

    [Fact]
    public void UserTextCleanedToNothing_IsNoRecord()
    {
        Assert.Empty(Map(User("\"<system-reminder>only this</system-reminder>\"")));
        Assert.Empty(Map(User("[{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}]")));
    }

    [Fact]
    public void UserBlocksAreCleanedOneByOne()
    {
        // an unclosed block in one text block must not swallow the next block
        var r = Single(Map(User("[{\"type\":\"text\",\"text\":\"<system-reminder>never closed\"},{\"type\":\"text\",\"text\":\"kept words\"}]")));
        Assert.Equal("kept words", r.Text);
    }

    [Fact]
    public void AssistantText_IsAssistantText()
    {
        var r = Single(Map(Assistant("{\"type\":\"text\",\"text\":\"  I will read <system-reminder>x</system-reminder> it.\\n\"}")));
        Assert.Equal(RecordKind.AssistantText, r.Kind);
        Assert.Equal("  I will read <system-reminder>x</system-reminder> it.\n", r.Text);   // as written
    }

    [Fact]
    public void AssistantEmptyText_IsNothing()
    {
        Assert.Empty(Map(Assistant("{\"type\":\"text\",\"text\":\"\"},{\"type\":\"text\",\"text\":\"  \\n\"},{\"type\":\"text\"}")));
    }

    [Fact]
    public void AssistantBlocks_InOrder()
    {
        var records = Map(Assistant(
            "{\"type\":\"text\",\"text\":\"before\"}," + ToolUse("Read", "{\"file_path\":\"a.cs\"}") + ",{\"type\":\"text\",\"text\":\"after\"}"));
        Assert.Equal(new[] { RecordKind.AssistantText, RecordKind.ToolStub, RecordKind.AssistantText }, records.Select(r => r.Kind));
        Assert.Equal(new[] { "before", "a.cs", "after" }, records.Select(r => r.Text));
        Assert.Equal("Read", records[1].ToolName);
        Assert.All(records, r => Assert.Equal((10L, 99L), (r.StartOffset, r.EndOffset)));
    }

    [Fact]
    public void StructuredOutput_IsKeyValueAssistantText()
    {
        var r = Single(Map(Assistant(ToolUse("StructuredOutput", "{\"verdict\":\"ok\",\"n\":3,\"items\":[\"a\"]}"))));
        Assert.Equal(RecordKind.AssistantText, r.Kind);
        Assert.Null(r.ToolName);
        Assert.Equal("verdict: ok\nn: 3\nitems: [\"a\"]", r.Text);
    }

    [Fact]
    public void StructuredOutput_NonAsciiStaysReadable_AndNestedObjectsAreCompact()
    {
        var r = Single(Map(Assistant(ToolUse("StructuredOutput", "{\"note\":\"caf\\u00e9 <b>\",\"o\": { \"k\" : 1 , \"t\" : true } , \"z\":null}"))));
        Assert.Equal("note: café <b>\no: {\"k\":1,\"t\":true}\nz: null", r.Text);
    }

    [Fact]
    public void StructuredOutput_Empty_IsNothing()
    {
        Assert.Empty(Map(Assistant(ToolUse("StructuredOutput", "{}"))));
        Assert.Empty(Map(Assistant("{\"type\":\"tool_use\",\"name\":\"StructuredOutput\"}")));
    }

    [Fact]
    public void ExitPlanMode_IsPlanAsAssistantText()
    {
        var r = Single(Map(Assistant(ToolUse("ExitPlanMode", "{\"plan\":\"1. Add the column.\\n2. Backfill.\"}"))));
        Assert.Equal(RecordKind.AssistantText, r.Kind);
        Assert.Null(r.ToolName);
        Assert.Equal("1. Add the column.\n2. Backfill.", r.Text);
        Assert.Empty(Map(Assistant(ToolUse("ExitPlanMode", "{\"plan\":\"\"}"))));
        Assert.Empty(Map(Assistant(ToolUse("ExitPlanMode", "{}"))));
    }

    [Theory]
    [InlineData("Bash", "{\"command\":\"ls -l\",\"description\":\"d\"}", "ls -l")]
    [InlineData("Read", "{\"offset\":1,\"file_path\":\"src/a.cs\"}", "src/a.cs")]
    [InlineData("Edit", "{\"old_string\":\"x\",\"file_path\":\"src/e.cs\"}", "src/e.cs")]
    [InlineData("Write", "{\"content\":\"x\",\"file_path\":\"src/w.cs\"}", "src/w.cs")]
    [InlineData("NotebookEdit", "{\"new_source\":\"x\",\"file_path\":\"n.ipynb\"}", "n.ipynb")]
    [InlineData("Grep", "{\"path\":\"src\",\"pattern\":\"TODO\"}", "TODO")]
    [InlineData("Glob", "{\"path\":\"src\",\"pattern\":\"**/*.cs\"}", "**/*.cs")]
    [InlineData("WebFetch", "{\"prompt\":\"p\",\"url\":\"https://example.com/x\"}", "https://example.com/x")]
    [InlineData("WebSearch", "{\"allowed_domains\":[\"a\"],\"query\":\"dotnet aot\"}", "dotnet aot")]
    [InlineData("Task", "{\"prompt\":\"long\",\"description\":\"find usages\"}", "find usages")]
    [InlineData("Agent", "{\"prompt\":\"long\",\"description\":\"review diff\"}", "review diff")]
    [InlineData("AskUserQuestion", "{\"questions\":[{\"question\":\"Which approach?\",\"header\":\"h\"},{\"question\":\"second\"}]}", "Which approach?")]
    public void ToolUse_DetailByToolName(string tool, string input, string expected)
    {
        var r = Single(Map(Assistant(ToolUse(tool, input))));
        Assert.Equal(RecordKind.ToolStub, r.Kind);
        Assert.Equal(tool, r.ToolName);
        Assert.Equal(expected, r.Text);
    }

    [Fact]
    public void ToolUse_DetailIsRaw_NotFoldedOrCut()
    {
        var long300 = new string('x', 300);
        var r = Single(Map(Assistant(ToolUse("Bash", "{\"command\":\"a\\n  b\\n" + long300 + "\"}"))));
        Assert.Equal("a\n  b\n" + long300, r.Text);
    }

    [Fact]
    public void UnknownTool_FirstStringFieldInDocumentOrder()
    {
        var r = Single(Map(Assistant(ToolUse("mcp__fixture__lookup", "{\"flag\":true,\"count\":3,\"note\":\"first string\",\"other\":\"second\"}"))));
        Assert.Equal(RecordKind.ToolStub, r.Kind);
        Assert.Equal("mcp__fixture__lookup", r.ToolName);
        Assert.Equal("first string", r.Text);
    }

    [Theory]
    [InlineData("Bash", "{}")]
    [InlineData("Bash", "{\"command\":\"\"}")]
    [InlineData("Bash", "{\"command\":5}")]
    [InlineData("Bash", "{\"description\":\"only description\"}")]
    [InlineData("Bash", "[1]")]
    [InlineData("Bash", "\"a string input\"")]
    [InlineData("AskUserQuestion", "{\"questions\":[]}")]
    [InlineData("AskUserQuestion", "{\"questions\":[{\"header\":\"h\"}]}")]
    [InlineData("AskUserQuestion", "{\"questions\":\"q\"}")]
    [InlineData("Custom", "{\"n\":1,\"b\":true}")]
    [InlineData("Custom", "{\"s\":\"\",\"t\":\"later\"}")]
    public void ToolUse_MissingOrEmptyField_NullText(string tool, string input)
    {
        var r = Single(Map(Assistant(ToolUse(tool, input))));
        Assert.Equal(RecordKind.ToolStub, r.Kind);
        Assert.Equal(tool, r.ToolName);
        Assert.Null(r.Text);
    }

    [Fact]
    public void ToolUse_NoInput_IsStubWithoutText()
    {
        var r = Single(Map(Assistant("{\"type\":\"tool_use\",\"name\":\"Bash\"}")));
        Assert.Null(r.Text);
    }

    [Fact]
    public void ToolUse_WithoutName_IsNothing()
    {
        Assert.Empty(Map(Assistant("{\"type\":\"tool_use\",\"input\":{\"command\":\"x\"}},{\"type\":\"tool_use\",\"name\":\"\"},{\"type\":\"tool_use\",\"name\":4}")));
    }

    [Fact]
    public void ThinkingImageOtherBlocks_AreNothing()
    {
        Assert.Empty(Map(Assistant(
            "{\"type\":\"thinking\",\"thinking\":\"zqxthinking\"},{\"type\":\"redacted_thinking\",\"data\":\"x\"},{\"type\":\"image\",\"source\":{}},{\"type\":\"server_tool_use\",\"name\":\"web\"},{\"type\":\"unknown\",\"text\":\"zq\"},5,\"str\",null")));
    }

    [Fact]
    public void AssistantStringContent_IsAssistantText()
    {
        var r = Single(Map("{\"type\":\"assistant\",\"message\":{\"content\":\"plain answer\"}}"));
        Assert.Equal(RecordKind.AssistantText, r.Kind);
        Assert.Equal("plain answer", r.Text);
    }

    [Fact]
    public void CompactBoundary_IsBoundary()
    {
        var r = Single(Map("{\"type\":\"system\",\"subtype\":\"compact_boundary\"," + Ts + ",\"content\":\"Conversation compacted\"}"));
        Assert.Equal(RecordKind.CompactionBoundary, r.Kind);
        Assert.Null(r.Text);
        Assert.Null(r.ToolName);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 5, TimeSpan.Zero), r.Ts);
    }

    [Theory]
    [InlineData("local_command")]
    [InlineData("informational")]
    [InlineData("")]
    public void SystemOtherSubtype_IsNothing(string subtype)
    {
        Assert.Empty(Map("{\"type\":\"system\",\"subtype\":\"" + subtype + "\",\"content\":\"x\"}"));
        Assert.Empty(Map("{\"type\":\"system\",\"content\":\"x\"}"));
        Assert.Empty(Map("{\"type\":\"system\",\"subtype\":7}"));
    }

    [Fact]
    public void IsCompactSummary_IsSummary_NotUserText()
    {
        var s = Single(Map(User("\"This session is being continued ...\"", ",\"isCompactSummary\":true")));
        Assert.Equal(RecordKind.CompactionSummary, s.Kind);
        Assert.Equal("This session is being continued ...", s.Text);

        var b = Single(Map(User("[{\"type\":\"text\",\"text\":\"part one\"},{\"type\":\"text\",\"text\":\"part two\"}]", ",\"isCompactSummary\":true")));
        Assert.Equal(RecordKind.CompactionSummary, b.Kind);
        Assert.Equal("part one\n\npart two", b.Text);
    }

    [Fact]
    public void IsCompactSummary_False_IsUserText()
    {
        Assert.Equal(RecordKind.UserText, Single(Map(User("\"hello\"", ",\"isCompactSummary\":false"))).Kind);
    }

    [Theory]
    [InlineData("user", "\"zqxmeta\"", "isMeta")]
    [InlineData("user", "\"zqxsidechain\"", "isSidechain")]
    [InlineData("assistant", "[{\"type\":\"text\",\"text\":\"zqxmeta\"}]", "isMeta")]
    [InlineData("assistant", "[{\"type\":\"text\",\"text\":\"zqxsidechain\"}]", "isSidechain")]
    [InlineData("user", "[{\"type\":\"tool_result\",\"is_error\":true,\"content\":\"zqxmeta\"}]", "isSidechain")]
    public void MetaAndSidechain_AreNothing(string type, string content, string flag)
    {
        Assert.Empty(Map("{\"type\":\"" + type + "\",\"" + flag + "\":true,\"message\":{\"content\":" + content + "}}"));
        Assert.NotEmpty(Map("{\"type\":\"" + type + "\",\"" + flag + "\":false,\"message\":{\"content\":" + content + "}}"));
    }

    [Fact]
    public void MetaCompactSummary_IsNothing()
    {
        Assert.Empty(Map(User("\"s\"", ",\"isMeta\":true,\"isCompactSummary\":true")));
    }

    [Theory]
    [InlineData("last-prompt")]
    [InlineData("file-history-snapshot")]
    [InlineData("cost-state")]
    [InlineData("api-request-start")]
    [InlineData("queue-operation")]
    [InlineData("summary")]
    [InlineData("")]
    [InlineData("User")]
    public void OtherRecordTypes_AreNothing(string type)
    {
        Assert.Empty(Map("{\"type\":\"" + type + "\",\"message\":{\"content\":\"zqx\"},\"content\":\"zqx\"}"));
    }

    [Fact]
    public void OtherAttachmentType_IsNothing()
    {
        Assert.Empty(Map(Queued("{\"type\":\"hook_success\",\"content\":\"zqx\"}")));
    }

    [Fact]
    public void Timestamp_WithOffset_NormalisedToUtc()
    {
        var r = Single(Map("{\"type\":\"user\",\"timestamp\":\"2026-03-05T10:00:00.000+02:00\",\"message\":{\"content\":\"x\"}}"));
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 8, 0, 0, TimeSpan.Zero), r.Ts);
        Assert.Equal(TimeSpan.Zero, r.Ts!.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"timestamp\":null")]
    [InlineData(",\"timestamp\":12")]
    [InlineData(",\"timestamp\":\"not a time\"")]
    public void NoTimestamp_TsNull(string timestamp)
    {
        var r = Single(Map("{\"type\":\"user\"" + timestamp + ",\"message\":{\"content\":\"x\"}}"));
        Assert.Null(r.Ts);
    }

    [Fact]
    public void Offsets_AreTheLines()
    {
        var records = Map(Assistant("{\"type\":\"text\",\"text\":\"a\"}," + ToolUse("Bash", "{\"command\":\"c\"}")), 1234, 5678);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal((1234L, 5678L), (r.StartOffset, r.EndOffset)));
    }

    [Fact]
    public void UserWithoutMessage_IsNothing()
    {
        Assert.Empty(Map("{\"type\":\"user\"}"));
        Assert.Empty(Map("{\"type\":\"assistant\"}"));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"x\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":5}")]
    [InlineData("{\"type\":null}")]
    [InlineData("{\"type\":\"user\",\"message\":\"x\"}")]
    [InlineData("{\"type\":\"user\",\"message\":null}")]
    [InlineData("{\"type\":\"last-prompt\",\"message\":[]}")]
    public void MalformedRecord_Throws(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => ClaudeRecordMapper.Map(doc.RootElement, 0, 1));
    }

    [Theory]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":\"a \\ud800 b\"}}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"\\udc00\"}]}}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"\\ud800\"}}]}}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"is_error\":true,\"content\":\"\\ud800\"}]}}")]
    [InlineData("{\"type\":\"user\\ud800\"}")]
    public void UndecodableString_Throws(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => ClaudeRecordMapper.Map(doc.RootElement, 0, 1));
    }

    [Fact]
    public void UndecodableStringInStructuredOutput_Throws_OrIsPlainText()
    {
        using var doc = JsonDocument.Parse(
            Assistant(ToolUse("StructuredOutput", "{\"a\":\"\\ud800\"}")));
        // The rendering must either reject the string (a counted line) or carry it through; it must never fail another way.
        try
        {
            var records = ClaudeRecordMapper.Map(doc.RootElement, 0, 1);
            Assert.Single(records);
        }
        catch (InvalidOperationException) { }
    }

    [Fact]
    public void UndecodableStringInIgnoredPlaces_IsNotAnError()
    {
        // Thinking blocks and successful tool results are never read, so a bad string there does not skip the line.
        var records = Map(Assistant("{\"type\":\"thinking\",\"thinking\":\"\\ud800\"},{\"type\":\"text\",\"text\":\"fine\"}"));
        Assert.Equal("fine", Single(records).Text);
        Assert.Empty(Map(User("[{\"type\":\"tool_result\",\"content\":\"\\ud800\"}]")));
    }
}
