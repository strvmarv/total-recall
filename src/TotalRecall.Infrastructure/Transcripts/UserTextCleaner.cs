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
/// the next one, or its absence, is remembered per tag name, and the closers of the whole <c>ide_*</c> family are indexed
/// in one pass), so the work stays linear however many unclosed tags the text holds. Command name and args blocks are
/// kept as <c>/name args</c>, <c>bash-input</c> commands are returned apart, every other listed block is dropped.
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
        IdeClosers? ide = null;                                       // built once, only if an <ide_*> opener needs a closer
        var afterName = -1;                                           // builder length right after the last command name, or -1
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

            var closer = FindCloser(text, closers, ref ide, name, afterOpen);
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
                    afterName = sb.Length;
                    break;
                case Tag.CommandArgs:
                    // The host lays the tags out over several indented lines; only white space between the name and
                    // the args is layout, so it is cut and the args follow the name after one space.
                    if (afterName >= 0 && IsWhiteSpaceFrom(sb, afterName))
                    {
                        sb.Length = afterName;
                        if (inner.Length > 0) sb.Append(' ').Append(inner);
                    }
                    else
                    {
                        afterName = -1;
                        if (inner.Length > 0)
                        {
                            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
                            sb.Append(inner);
                        }
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

    private static bool IsWhiteSpaceFrom(StringBuilder sb, int from)
    {
        for (var k = from; k < sb.Length; k++)
            if (!char.IsWhiteSpace(sb[k])) return false;
        return true;
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
    /// moves forward only, so an index at or after <paramref name="from"/> is still the first one and a -1 stays -1. The
    /// <c>ide_*</c> family shares one index of all its closers (<see cref="IdeClosers"/>), so distinct unclosed names do
    /// not each search to the end of the text.
    /// </summary>
    private static int FindCloser(string text, Dictionary<string, int> closers, ref IdeClosers? ide, string name, int from)
    {
        if (name.StartsWith("ide_", StringComparison.Ordinal))
            return (ide ??= new IdeClosers(text)).Next(name, from);
        if (closers.TryGetValue(name, out var known) && (known < 0 || known >= from)) return known;
        var found = text.IndexOf("</" + name + ">", from, StringComparison.Ordinal);
        closers[name] = found;
        return found;
    }

    /// <summary>Every <c>&lt;/ide_[a-z_]+&gt;</c> of a text by name, found in one pass; a lookup is a binary search.</summary>
    private sealed class IdeClosers
    {
        private readonly Dictionary<string, List<int>> _byName = new(StringComparer.Ordinal);

        public IdeClosers(string text)
        {
            var pos = 0;
            while ((pos = text.IndexOf("</ide_", pos, StringComparison.Ordinal)) >= 0)
            {
                var j = pos + 6;
                while (j < text.Length && (text[j] is (>= 'a' and <= 'z') or '_')) j++;
                if (j > pos + 6 && j < text.Length && text[j] == '>')
                {
                    var name = text.Substring(pos + 2, j - pos - 2);
                    if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<int>();
                    list.Add(pos);
                }
                pos += 2;
            }
        }

        public int Next(string name, int from)
        {
            if (!_byName.TryGetValue(name, out var list)) return -1;
            var k = list.BinarySearch(from);
            if (k < 0) k = ~k;
            return k < list.Count ? list[k] : -1;
        }
    }
}
