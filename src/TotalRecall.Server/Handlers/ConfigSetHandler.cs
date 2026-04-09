// src/TotalRecall.Server/Handlers/ConfigSetHandler.cs
//
// Plan 6 Task 6.0d — ports `total-recall config set <key> <value>` to MCP.
// Writes a user override via ConfigWriter.SaveUserOverride (AOT-safe hand-
// rolled TOML writer) and returns {key, old_value, new_value, written}.
//
// Args:
//   key   (string, required, dotted path)
//   value (JSON, required; accepts string/number/bool; JSON null removes
//          the override — but note ConfigWriter doesn't have a delete path
//          today, so we throw NotSupportedException on null and document
//          the gap. The CLI SetCommand does not support null either; this
//          matches behavior.)
//
// The handler does NOT take a pre-change config_snapshot (the CLI
// SetCommand does so best-effort via ConfigSnapshotStore). Snapshotting is
// a CLI-level convenience; the MCP surface stays lean and host tools that
// want snapshots can call eval_snapshot explicitly before config_set.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn.Model;
using TotalRecall.Infrastructure.Config;

namespace TotalRecall.Server.Handlers;

public sealed class ConfigSetHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "key":   {"type":"string","description":"Dotted config key"},
            "value": {"description":"New value (string/number/bool)"}
          },
          "required": ["key","value"]
        }
        """).RootElement.Clone();

    private readonly ConfigTableProvider _tableProvider;
    private readonly string? _userConfigPath;

    public ConfigSetHandler()
    {
        _tableProvider = () => new ConfigLoader().LoadEffectiveTable();
        _userConfigPath = null;
    }

    /// <summary>Test/composition seam.</summary>
    public ConfigSetHandler(ConfigTableProvider tableProvider, string userConfigPath)
    {
        _tableProvider = tableProvider ?? throw new ArgumentNullException(nameof(tableProvider));
        _userConfigPath = userConfigPath ?? throw new ArgumentNullException(nameof(userConfigPath));
    }

    public string Name => "config_set";
    public string Description => "Write a user-override config value (dotted key)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("config_set requires arguments object");

        var args = arguments.Value;
        if (!args.TryGetProperty("key", out var kEl) || kEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("key is required");
        var key = kEl.GetString();
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key must be a non-empty string");

        if (!args.TryGetProperty("value", out var vEl))
            throw new ArgumentException("value is required");

        var coerced = CoerceJsonValue(vEl);
        if (coerced is null)
            throw new NotSupportedException(
                "config_set does not support null values (override deletion is not supported; "
                + "remove the key from the user config.toml manually)");

        ct.ThrowIfCancellationRequested();

        // Capture old value for the response envelope.
        var beforeTable = _tableProvider();
        var oldValue = ConfigWriter.GetNestedValue(beforeTable, key);
        var oldValueText = FormatForWire(oldValue);

        var userConfigPath = _userConfigPath
            ?? Path.Combine(ConfigLoader.GetDataDir(), "config.toml");

        ConfigWriter.SaveUserOverride(userConfigPath, key, coerced);

        var dto = new ConfigSetResultDto(
            Key: key,
            OldValue: oldValueText,
            NewValue: FormatForWire(coerced),
            Written: true);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.ConfigSetResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    /// <summary>
    /// Coerce a JSON value element into a TOML-writable scalar
    /// (bool/long/double/string). Mirrors SetCommand.Coerce but accepts
    /// native JSON types instead of re-parsing a raw string.
    /// </summary>
    internal static object? CoerceJsonValue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return el.GetString() ?? string.Empty;
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                return el.GetDouble();
            default:
                throw new ArgumentException(
                    $"value must be a string/number/bool (got {el.ValueKind})");
        }
    }

    private static string? FormatForWire(object? value)
    {
        if (value is null) return null;
        return value switch
        {
            bool b => b ? "true" : "false",
            string s => s,
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float f => ((double)f).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            TomlTable => "<table>",
            TomlArray => "<array>",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }
}
