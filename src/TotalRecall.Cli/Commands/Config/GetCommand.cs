// src/TotalRecall.Cli/Commands/Config/GetCommand.cs
//
// Plan 5 Task 5.8 — `total-recall config get [key] [--json]`. Ports
// src-ts/tools/system-tools.ts:136-149 (config_get). With no key, prints
// the full merged config. With a dotted key, walks the merged TomlTable
// and prints {key, value}. Missing keys go to stderr with exit 1.
//
// Walks the raw TomlTable (via IConfigLoader.LoadEffectiveTable) rather
// than the projected TotalRecallConfig so callers can query arbitrary
// keys including those not in the F# record schema.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Tomlyn.Model;
using TotalRecall.Infrastructure.Config;

namespace TotalRecall.Cli.Commands.Config;

public sealed class GetCommand : ICliCommand
{
    private readonly IConfigLoader? _loader;
    private readonly TextWriter? _out;

    public GetCommand() { }

    // Test/composition seam.
    public GetCommand(IConfigLoader loader, TextWriter output)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "get";
    public string? Group => "config";
    public string Description => "Print the full config or a specific key (dotted path)";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        bool emitJson = false;
        string? key = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json":
                    emitJson = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"config get: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (key is not null)
                    {
                        Console.Error.WriteLine($"config get: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    key = a;
                    break;
            }
        }

        IConfigLoader loader = _loader ?? new ConfigLoader();
        TomlTable table;
        try
        {
            table = loader.LoadEffectiveTable();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config get: failed to load config: {ex.Message}");
            return 1;
        }

        var writer = _out ?? Console.Out;

        if (string.IsNullOrEmpty(key))
        {
            if (emitJson)
            {
                writer.WriteLine(SerializeTableJson(table));
            }
            else
            {
                writer.Write(ConfigWriter.SerializeTomlTable(table));
            }
            return 0;
        }

        var value = ConfigWriter.GetNestedValue(table, key);
        if (value is null)
        {
            Console.Error.WriteLine($"key not found: {key}");
            return 1;
        }

        if (emitJson)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            AppendJsonString(sb, "key"); sb.Append(':');
            AppendJsonString(sb, key); sb.Append(',');
            AppendJsonString(sb, "value"); sb.Append(':');
            AppendJsonValue(sb, value);
            sb.Append('}');
            writer.WriteLine(sb.ToString());
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append(key).Append(" = ");
            AppendTomlValue(sb, value);
            writer.WriteLine(sb.ToString());
        }
        return 0;
    }

    private static void AppendTomlValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("(null)");
                break;
            case TomlTable t:
                sb.Append('{').Append(string.Join(", ", CollectLeafKeys(t))).Append('}');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                sb.Append('"').Append(s).Append('"');
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static IEnumerable<string> CollectLeafKeys(TomlTable t)
    {
        foreach (var kv in t) yield return kv.Key;
    }

    // --- JSON emission (hand-rolled, AOT-safe) ---------------------------

    private static string SerializeTableJson(TomlTable table)
    {
        var sb = new StringBuilder();
        AppendJsonValue(sb, table);
        return sb.ToString();
    }

    private static void AppendJsonValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case TomlTable t:
                sb.Append('{');
                bool first = true;
                foreach (var kv in t)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendJsonString(sb, kv.Key);
                    sb.Append(':');
                    AppendJsonValue(sb, kv.Value);
                }
                sb.Append('}');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                AppendJsonString(sb, s);
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture));
                break;
            case TomlArray arr:
                sb.Append('[');
                bool firstA = true;
                foreach (var item in arr)
                {
                    if (!firstA) sb.Append(',');
                    firstA = false;
                    AppendJsonValue(sb, item);
                }
                sb.Append(']');
                break;
            default:
                AppendJsonString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall config get [key] [--json]");
    }
}
