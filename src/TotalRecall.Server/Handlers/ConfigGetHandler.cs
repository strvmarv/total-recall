// src/TotalRecall.Server/Handlers/ConfigGetHandler.cs
//
// Plan 6 Task 6.0d — ports `total-recall config get [key]` to MCP.
// Mirrors the CLI's --json wire shape. Two modes:
//   1. no key   -> {config: <full merged TomlTable as JSON>}
//   2. with key -> {key, value}  (value may be scalar, array, or nested table)
//
// Unknown key is reported as a thrown ArgumentException (ErrorTranslator
// turns that into an MCP isError response); mirroring the CLI's "key not
// found" branch which exits non-zero.
//
// AOT note: the value tree is a TomlTable / TomlArray / scalar soup.
// Rather than declaring source-gen DTOs for every shape, we hand-build
// a JsonElement via Utf8JsonWriter (reflection-free, AOT-safe) and then
// source-gen-serialize the wrapping DTO via JsonContext.

using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn.Model;
using TotalRecall.Infrastructure.Config;

namespace TotalRecall.Server.Handlers;

/// <summary>Test seam for <see cref="ConfigGetHandler"/>.</summary>
public delegate TomlTable ConfigTableProvider();

public sealed class ConfigGetHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "key": {"type":"string","description":"Dotted config key (optional; omit for full config)"}
          }
        }
        """).RootElement.Clone();

    private readonly ConfigTableProvider _provider;

    public ConfigGetHandler()
    {
        _provider = () => new ConfigLoader().LoadEffectiveTable();
    }

    /// <summary>Test/composition seam.</summary>
    public ConfigGetHandler(ConfigTableProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "config_get";
    public string Description => "Return the full effective config, or a specific dotted key";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        string? key = null;
        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            if (arguments.Value.TryGetProperty("key", out var kEl) && kEl.ValueKind == JsonValueKind.String)
            {
                var k = kEl.GetString();
                if (!string.IsNullOrEmpty(k)) key = k;
            }
        }

        ct.ThrowIfCancellationRequested();

        var table = _provider();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (key is null)
            {
                writer.WritePropertyName("config");
                WriteTomlValue(writer, table);
            }
            else
            {
                var value = ConfigWriter.GetNestedValue(table, key);
                if (value is null)
                {
                    throw new ArgumentException($"config key not found: {key}");
                }
                writer.WriteString("key", key);
                writer.WritePropertyName("value");
                WriteTomlValue(writer, value);
            }
            writer.WriteEndObject();
        }

        var jsonText = Encoding.UTF8.GetString(stream.ToArray());
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static void WriteTomlValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case TomlTable t:
                w.WriteStartObject();
                foreach (var kv in t)
                {
                    w.WritePropertyName(kv.Key);
                    WriteTomlValue(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case TomlArray arr:
                w.WriteStartArray();
                foreach (var item in arr) WriteTomlValue(w, item);
                w.WriteEndArray();
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case float f:
                w.WriteNumberValue(f);
                break;
            default:
                w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }
}
