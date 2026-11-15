// src/TotalRecall.Cli/Commands/WhatsNewCommand.cs
//
// `total-recall whats-new --host <id> [--format text|json|claude-code] [--no-mark]`.
// Backend for the hosts that deliver the what's-new notice themselves (the Claude Code
// hook, the pi extension): prints the notice for one host, then records that the host
// was told. The host comes only from --host; the command reads no environment variable,
// applies no Hermes rule and ignores the direct-delivery stamps (the caller already
// chose to deliver directly). FAIL-SAFE: any error prints the format's empty output
// ("" for text, "{}" for the JSON formats) and exits 0.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;
using TotalRecall.Infrastructure.WhatsNew;

namespace TotalRecall.Cli.Commands;

public sealed class WhatsNewCommand : ICliCommand
{
    private readonly string? _version;
    private readonly string? _stateDir;
    private readonly string? _binaryDir;
    private readonly Func<bool>? _noticeEnabled;
    private readonly TextWriter? _out;

    /// <summary>Production: every input is resolved when the command runs, not when the registry is built.</summary>
    public WhatsNewCommand() { }

    public WhatsNewCommand(string version, string stateDir, string binaryDir, Func<bool> noticeEnabled, TextWriter output)
    {
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _stateDir = stateDir ?? throw new ArgumentNullException(nameof(stateDir));
        _binaryDir = binaryDir ?? throw new ArgumentNullException(nameof(binaryDir));
        _noticeEnabled = noticeEnabled ?? throw new ArgumentNullException(nameof(noticeEnabled));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "whats-new";
    public string? Group => null;
    public string Description => "Print the what's-new notice for one host once (--host <id> [--format text|json|claude-code] [--no-mark])";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private enum Format { Text, Json, ClaudeCode }

    private int Execute(string[] args)
    {
        var output = _out ?? Console.Out;
        var format = Format.Text;
        try
        {
            var hostArg = ValueOf(args, "--host");
            format = ParseFormat(ValueOf(args, "--format"));
            var mark = Array.IndexOf(args, "--no-mark") < 0;

            var host = HostIds.Normalize(hostArg);
            var stateDir = _stateDir ?? WhatsNewState.DefaultDir();
            var binaryDir = _binaryDir ?? AppContext.BaseDirectory;
            var version = _version ?? CliApp.CurrentVersion;
            var enabled = _noticeEnabled is null ? ConfigLoader.LoadUpdatesNoticeEnabled() : _noticeEnabled();

            var seen = WhatsNewState.ReadSeenVersion(stateDir, host);
            var notes = PackagedReleaseNotes.Read(binaryDir);
            var notice = WhatsNewDecider.Decide(version, notes, seen, enabled);

            var rendered = notice is null ? Empty(format) : Render(format, notice);
            if (rendered.Length > 0)
            {
                output.WriteLine(rendered);
                output.Flush();
            }

            // Record only after the notice was written, so one that never reached the host is offered again.
            if (notice is not null && mark)
                WhatsNewState.WriteSeenVersion(stateDir, host, notice.Version);
            return 0;
        }
        catch
        {
            try
            {
                var empty = Empty(format);
                if (empty.Length > 0) output.WriteLine(empty);
            }
            catch { }
            return 0;
        }
    }

    /// <summary>The value following <paramref name="flag"/>; null when the flag is absent, last, or followed by another flag.</summary>
    private static string? ValueOf(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != flag) continue;
            var next = args[i + 1];
            return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
        }
        return null;
    }

    private static Format ParseFormat(string? value) => value switch
    {
        "json" => Format.Json,
        "claude-code" => Format.ClaudeCode,
        _ => Format.Text,
    };

    private static string Empty(Format format) => format == Format.Text ? "" : "{}";

    private static string Render(Format format, Notice notice)
    {
        var line = WhatsNewRenderer.Line(notice);
        if (format == Format.Text) return line;

        var sb = new StringBuilder();
        sb.Append('{');
        if (format == Format.Json)
        {
            JsonWriter.AppendString(sb, "kind"); sb.Append(':');
            JsonWriter.AppendString(sb, WhatsNewRenderer.KindName(notice.Kind)); sb.Append(',');
            JsonWriter.AppendString(sb, "text");
        }
        else
        {
            JsonWriter.AppendString(sb, "systemMessage");
        }
        sb.Append(':');
        JsonWriter.AppendString(sb, line);
        sb.Append('}');
        return sb.ToString();
    }
}
