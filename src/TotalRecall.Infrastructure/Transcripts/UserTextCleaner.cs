using System;
using System.Collections.Generic;
using System.Text;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// What is left of a user text once host-injected blocks are removed: <see cref="Text"/> is null when nothing is left,
/// <see cref="ShellInputs"/> are the commands of <c>&lt;bash-input&gt;</c> blocks (the <c>!</c> shell mode), in order.
/// </summary>
internal sealed record CleanedUserText(string? Text, IReadOnlyList<string> ShellInputs);

/// <summary>
/// The dropped-text rules for <b>user</b> text (spec section 3). One left-to-right pass over the string with ordinal
/// comparisons only (no regular expression): a closer is looked up at most once per stretch of text (the position of
/// the next one, or its absence, is remembered per tag name), so the work stays linear however many unclosed tags the
/// text holds.
/// </summary>
internal static class UserTextCleaner
{
    private const string BashInput = "bash-input";
    private const string CommandName = "command-name";
    private const string CommandArgs = "command-args";
    private const string InterruptionPrefix = "[Request interrupted by user";

    // Whole blocks, tags included, dropped. <ide_[a-z_]+> blocks are dropped as well (see TryReadOpenTag).
    private static readonly string[] Dropped =
    {
        "system-reminder",
        "task-notification",
        "local-command-stdout",
        "local-command-stderr",
        "local-command-caveat",
        "command-message",
        "bash-stdout",
        "bash-stderr",
        "EXTREMELY_IMPORTANT",
    };

    private enum Tag { Dropped, BashInput, CommandName, CommandArgs }

    public static CleanedUserText Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        List<string>? shell = null;
        var closers = new Dictionary<string, int>(StringComparer.Ordinal);   // name -> index of its next closer, or -1
        var n = text.Length;
        var i = 0;

        while (i < n)
        {
            var lt = text.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(text, i, n - i);
                break;
            }
            sb.Append(text, i, lt - i);

            if (!TryReadOpenTag(text, lt, out var name, out var tag, out var afterOpen))
            {
                sb.Append('<');
                i = lt + 1;
                continue;
            }

            var closer = FindCloser(text, closers, name, afterOpen);
            if (closer < 0)
            {
                // Dropped block with no closer: gone to the end of the text only when its opener starts the text or a line.
                if (tag == Tag.Dropped && (lt == 0 || text[lt - 1] == '\n'))
                    break;
                sb.Append(text, lt, afterOpen - lt);      // anything else unclosed is plain text
                i = afterOpen;
                continue;
            }

            var inner = text.AsSpan(afterOpen, closer - afterOpen).Trim();
            switch (tag)
            {
                case Tag.BashInput:
                    if (inner.Length > 0) (shell ??= new List<string>()).Add(inner.ToString());
                    break;
                case Tag.CommandName:
                    sb.Append(inner);
                    break;
                case Tag.CommandArgs:
                    if (inner.Length > 0)
                    {
                        if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
                        sb.Append(inner);
                    }
                    break;
            }
            i = closer + name.Length + 3;                 // "</" + name + ">"
        }

        var cleaned = sb.ToString().Trim();
        var shellInputs = (IReadOnlyList<string>?)shell ?? Array.Empty<string>();
        if (cleaned.Length == 0 || IsInterruptionNotice(cleaned)) return new CleanedUserText(null, shellInputs);
        return new CleanedUserText(cleaned, shellInputs);
    }

    /// <summary>A text that is one host notice such as <c>[Request interrupted by user for tool use]</c> and nothing else.</summary>
    private static bool IsInterruptionNotice(string s) =>
        s.StartsWith(InterruptionPrefix, StringComparison.Ordinal)
        && s[^1] == ']'
        && s.IndexOf(']') == s.Length - 1;

    /// <summary>Reads <c>&lt;name&gt;</c> at <paramref name="lt"/> when the name is one the cleaner acts on.</summary>
    private static bool TryReadOpenTag(string text, int lt, out string name, out Tag tag, out int afterOpen)
    {
        var rest = text.AsSpan(lt + 1);

        foreach (var d in Dropped)
        {
            if (IsOpenTagOf(rest, d))
            {
                name = d;
                tag = Tag.Dropped;
                afterOpen = lt + 1 + d.Length + 1;
                return true;
            }
        }
        if (IsOpenTagOf(rest, BashInput)) return Found(BashInput, Tag.BashInput, lt, out name, out tag, out afterOpen);
        if (IsOpenTagOf(rest, CommandName)) return Found(CommandName, Tag.CommandName, lt, out name, out tag, out afterOpen);
        if (IsOpenTagOf(rest, CommandArgs)) return Found(CommandArgs, Tag.CommandArgs, lt, out name, out tag, out afterOpen);

        // <ide_[a-z_]+>: the host's IDE context blocks.
        if (rest.StartsWith("ide_", StringComparison.Ordinal))
        {
            var j = 4;
            while (j < rest.Length && (rest[j] is (>= 'a' and <= 'z') or '_')) j++;
            if (j > 4 && j < rest.Length && rest[j] == '>')
            {
                name = rest[..j].ToString();
                tag = Tag.Dropped;
                afterOpen = lt + 1 + j + 1;
                return true;
            }
        }

        name = "";
        tag = default;
        afterOpen = 0;
        return false;
    }

    private static bool Found(string n, Tag t, int lt, out string name, out Tag tag, out int afterOpen)
    {
        name = n;
        tag = t;
        afterOpen = lt + 1 + n.Length + 1;
        return true;
    }

    private static bool IsOpenTagOf(ReadOnlySpan<char> rest, string name) =>
        rest.Length > name.Length && rest[name.Length] == '>' && rest.StartsWith(name, StringComparison.Ordinal);

    /// <summary>
    /// Index of the first <c>&lt;/name&gt;</c> at or after <paramref name="from"/>, or -1. The answer is remembered: scanning
    /// moves forward only, so an index at or after <paramref name="from"/> is still the first one and a -1 stays -1.
    /// </summary>
    private static int FindCloser(string text, Dictionary<string, int> closers, string name, int from)
    {
        if (closers.TryGetValue(name, out var known) && (known < 0 || known >= from)) return known;
        var found = text.IndexOf("</" + name + ">", from, StringComparison.Ordinal);
        closers[name] = found;
        return found;
    }
}
