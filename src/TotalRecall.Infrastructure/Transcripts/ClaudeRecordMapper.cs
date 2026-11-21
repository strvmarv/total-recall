using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Turns one parsed Claude Code JSONL record into zero or more <see cref="TranscriptRecord"/>s (spec section 2, the record
/// mapping table, and section 3). Texts are returned raw: redaction, folding, cutting and the stub wrapper belong to the
/// exchange builder.
/// </summary>
internal static class ClaudeRecordMapper
{
    private const string BlockSeparator = "\n\n";
    private const string Human = "human";

    private static readonly JsonWriterOptions CompactWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly IReadOnlyList<TranscriptRecord> None = Array.Empty<TranscriptRecord>();

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> for a line that is not a readable record (not an object, a
    /// <c>type</c> that is absent or not a string, a <c>message</c> that is present and not an object) and for a string it
    /// reads that cannot be decoded (a lone surrogate escape); the adapter counts such a line as skipped.
    /// </summary>
    public static IReadOnlyList<TranscriptRecord> Map(JsonElement root, long start, long end)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The record is not a JSON object.");
        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("The record has no string 'type'.");
        var type = typeElement.GetString();

        JsonElement message = default;
        var hasMessage = root.TryGetProperty("message", out message);
        if (hasMessage && message.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The record's 'message' is not an object.");

        // A string type outside the table is a routine host record, not a bad line.
        if (type is not ("user" or "assistant" or "attachment" or "system")) return None;
        if (IsTrue(root, "isMeta") || IsTrue(root, "isSidechain")) return None;

        var output = new List<TranscriptRecord>(2);
        var map = new Emitter(root, start, end, output);
        switch (type)
        {
            case "user":
                if (hasMessage) MapUser(root, message, map);
                break;
            case "assistant":
                if (hasMessage) MapAssistant(message, map);
                break;
            case "attachment":
                MapAttachment(root, map);
                break;
            case "system":
                if (root.TryGetProperty("subtype", out var subtype) && Is(subtype, "compact_boundary"))
                    map.Add(RecordKind.CompactionBoundary, null, null);
                break;
        }
        return output;
    }

    private static void MapUser(JsonElement root, JsonElement message, Emitter map)
    {
        message.TryGetProperty("content", out var content);

        if (IsTrue(root, "isCompactSummary"))
        {
            var summary = JoinTexts(content);
            if (!string.IsNullOrWhiteSpace(summary)) map.Add(RecordKind.CompactionSummary, summary, null);
            return;
        }

        // A record the host injected (a background task's result, ...) is not the user's, whatever it holds.
        if (root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object
            && origin.TryGetProperty("kind", out var kind) && !Is(kind, Human))
            return;

        var texts = new List<string>();
        var shell = new List<string>();
        var errors = new List<string>();

        if (content.ValueKind == JsonValueKind.String)
        {
            AddCleaned(content.GetString()!, texts, shell);
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var blockType)) continue;
                if (Is(blockType, "text"))
                {
                    if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        AddCleaned(t.GetString()!, texts, shell);
                }
                else if (Is(blockType, "tool_result") && IsTrue(block, "is_error"))
                {
                    errors.Add(block.TryGetProperty("content", out var c) ? JoinText(c, "") : "");
                }
            }
        }

        if (texts.Count > 0) map.Add(RecordKind.UserText, string.Join(BlockSeparator, texts), null);
        foreach (var command in shell) map.Add(RecordKind.ToolStub, command, "Bash");
        foreach (var error in errors) map.Add(RecordKind.ToolError, error, null);
    }

    private static void AddCleaned(string text, List<string> texts, List<string> shell)
    {
        var cleaned = UserTextCleaner.Clean(text);
        if (cleaned.Text is not null) texts.Add(cleaned.Text);
        shell.AddRange(cleaned.ShellInputs);
    }

    private static void MapAssistant(JsonElement message, Emitter map)
    {
        if (!message.TryGetProperty("content", out var content)) return;

        if (content.ValueKind == JsonValueKind.String)
        {
            AddAssistantText(content.GetString()!, map);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var blockType)) continue;
            if (Is(blockType, "text"))
            {
                if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    AddAssistantText(t.GetString()!, map);
            }
            else if (Is(blockType, "tool_use"))
            {
                MapToolUse(block, map);
            }
        }
    }

    private static void AddAssistantText(string text, Emitter map)
    {
        if (!string.IsNullOrWhiteSpace(text)) map.Add(RecordKind.AssistantText, text, null);
    }

    private static void MapToolUse(JsonElement block, Emitter map)
    {
        if (!block.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) return;
        var name = nameElement.GetString()!;
        if (name.Length == 0) return;
        var hasInput = block.TryGetProperty("input", out var input);

        switch (name)
        {
            case "StructuredOutput":
                // The whole answer of an SDK run (spec call 8): assistant text, not a stub.
                if (hasInput && RenderStructured(input) is { Length: > 0 } rendered)
                    map.Add(RecordKind.AssistantText, rendered, null);
                return;
            case "ExitPlanMode":
                if (hasInput && input.ValueKind == JsonValueKind.Object
                    && input.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.String
                    && plan.GetString() is { Length: > 0 } planText)
                    map.Add(RecordKind.AssistantText, planText, null);
                return;
            default:
                map.Add(RecordKind.ToolStub, hasInput ? ToolDetail(name, input) : null, name);
                return;
        }
    }

    /// <summary>The one field of a tool's input that its stub shows (spec section 3); null when missing or empty.</summary>
    private static string? ToolDetail(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        switch (tool)
        {
            case "Bash": return StringField(input, "command");
            case "Read":
            case "Edit":
            case "Write":
            case "NotebookEdit": return StringField(input, "file_path");
            case "Grep":
            case "Glob": return StringField(input, "pattern");
            case "WebFetch": return StringField(input, "url");
            case "WebSearch": return StringField(input, "query");
            case "Task":
            case "Agent": return StringField(input, "description");
            case "AskUserQuestion":
                if (input.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var first in questions.EnumerateArray())
                        return first.ValueKind == JsonValueKind.Object ? StringField(first, "question") : null;
                }
                return null;
            default:
                foreach (var property in input.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        return NullIfEmpty(property.Value.GetString());
                }
                return null;
        }
    }

    private static string? StringField(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? NullIfEmpty(v.GetString()) : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// A StructuredOutput input as <c>key: value</c> lines: strings as they are, every other value as compact JSON. An input
    /// that is not an object is its string, or its compact JSON.
    /// </summary>
    private static string RenderStructured(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String) return input.GetString()!;
        if (input.ValueKind != JsonValueKind.Object) return Compact(input);

        var sb = new StringBuilder();
        foreach (var property in input.EnumerateObject())
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(property.Name).Append(": ");
            sb.Append(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : Compact(property.Value));
        }
        return sb.ToString();
    }

    private static string Compact(JsonElement value)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, CompactWriter))
                value.WriteTo(writer);
            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or DecoderFallbackException)
        {
            throw new InvalidOperationException("A value could not be rendered.", e);
        }
    }

    private static void MapAttachment(JsonElement root, Emitter map)
    {
        // A prompt the user typed while the agent was working: every field sits inside 'attachment'.
        if (!root.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object) return;
        if (!attachment.TryGetProperty("type", out var type) || !Is(type, "queued_command")) return;
        if (!attachment.TryGetProperty("commandMode", out var mode) || !Is(mode, "prompt")) return;
        if (!attachment.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.Object) return;
        if (!origin.TryGetProperty("kind", out var kind) || !Is(kind, Human)) return;
        if (!attachment.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String) return;

        var texts = new List<string>();
        var shell = new List<string>();
        AddCleaned(prompt.GetString()!, texts, shell);
        if (texts.Count > 0) map.Add(RecordKind.UserText, texts[0], null);
        foreach (var command in shell) map.Add(RecordKind.ToolStub, command, "Bash");
    }

    /// <summary>A message content that is a string, or an array whose <c>text</c> blocks are joined with a blank line.</summary>
    private static string JoinTexts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        return JoinText(content, BlockSeparator);
    }

    /// <summary>A string as it is; an array as its <c>text</c> blocks joined with <paramref name="separator"/>; anything else empty.</summary>
    private static string JoinText(JsonElement content, string separator)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) return "";

        StringBuilder? sb = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type) || !Is(type, "text")
                || !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                continue;
            sb ??= new StringBuilder();
            if (sb.Length > 0) sb.Append(separator);
            sb.Append(text.GetString());
        }
        return sb?.ToString() ?? "";
    }

    /// <summary>True for a string element equal to <paramref name="value"/>; false for any other kind (ValueEquals throws on those).</summary>
    private static bool Is(JsonElement element, string value) =>
        element.ValueKind == JsonValueKind.String && element.ValueEquals(value);

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Adds records with the line's offsets and timestamp; the timestamp is read once, on the first record.</summary>
    private sealed class Emitter
    {
        private readonly JsonElement _root;
        private readonly long _start;
        private readonly long _end;
        private readonly List<TranscriptRecord> _output;
        private DateTimeOffset? _ts;
        private bool _tsRead;

        public Emitter(JsonElement root, long start, long end, List<TranscriptRecord> output)
        {
            _root = root;
            _start = start;
            _end = end;
            _output = output;
        }

        public void Add(RecordKind kind, string? text, string? toolName)
        {
            if (!_tsRead)
            {
                _tsRead = true;
                if (_root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
                    && TranscriptTime.TryParse(t.GetString(), out var parsed))
                    _ts = parsed;
            }
            _output.Add(new TranscriptRecord(kind, _start, _end, _ts, text, toolName));
        }
    }
}
