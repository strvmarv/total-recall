// Git sync, milestone 1b-iii parts 2 to 4 (held deletions, pin review and conflict review), D7 and D8: the hidden
// `sync review` verbs, a thin command over the review service. `list` prints every item (a held deletion, a new pin, a held
// pin edit, a conflict) with its kind and the memory's text as data between fixed delimiter lines, every text line behind
// `| ` so no line of it can end the block; `accept` and `restore` answer the held deletion of an id, `approve` and
// `decline` its pin item, `keep`, `swap` and `merge` its conflict item (these need the shown hash); the bulk forms loop
// over the held deletions only and run only on an interactive terminal. Results and refusals follow SyncCommandBase.Print
// (exit 0 or 1); a usage error exits 2.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary>
/// <c>total-recall sync review list | accept &lt;id&gt; [--shown &lt;hash&gt;] | restore &lt;id&gt; [--shown &lt;hash&gt;] |
/// approve &lt;id&gt; --shown &lt;hash&gt; | decline &lt;id&gt; --shown &lt;hash&gt; | keep &lt;id&gt; --shown &lt;hash&gt; |
/// swap &lt;id&gt; --shown &lt;hash&gt; [--loser &lt;n&gt;] | merge &lt;id&gt; --shown &lt;hash&gt; --text-file &lt;path|-&gt; |
/// accept-deletions --all | restore-deletions --all</c> (hidden; spec decisions D7 and D8): lists and answers the held
/// deletions, the pin review items and the conflict items of git sync.
/// </summary>
public sealed class ReviewCommand : ICliCommand
{
    private const string Usage =
        "Usage: total-recall sync review list | accept <id> [--shown <hash>] | restore <id> [--shown <hash>] | "
        + "approve <id> --shown <hash> | decline <id> --shown <hash> | "
        + "keep <id> --shown <hash> | swap <id> --shown <hash> [--loser <n>] | merge <id> --shown <hash> --text-file <path|-> | "
        + "accept-deletions --all | restore-deletions --all";

    /// <summary>The most bytes <c>merge --text-file</c> reads (the service's own cap on a merged text).</summary>
    private const int MaxTextFileBytes = 262144;

    private const string NeedsATerminal =
        "needs-a-terminal: the bulk forms answer every item and run only when standard input and standard output are a "
        + "terminal; answer items one at a time with accept or restore";

    private const string EndDelimiter = "----- end of memory text -----";

    private readonly Func<bool> _isTerminal;
    private readonly Func<Stream> _openStandardInput;

    /// <summary>The production command: the bulk forms run only when neither standard input nor standard output is redirected.</summary>
    public ReviewCommand()
        : this(() => !Console.IsInputRedirected && !Console.IsOutputRedirected)
    {
    }

    /// <summary>The command with the terminal test of the bulk forms injected (a test cannot fake a redirected console).</summary>
    public ReviewCommand(Func<bool> isTerminal)
        : this(isTerminal, Console.OpenStandardInput)
    {
    }

    /// <summary>The command with the terminal test and the standard input stream (<c>merge --text-file -</c>) injected.</summary>
    public ReviewCommand(Func<bool> isTerminal, Func<Stream> openStandardInput)
    {
        ArgumentNullException.ThrowIfNull(isTerminal);
        ArgumentNullException.ThrowIfNull(openStandardInput);
        _isTerminal = isTerminal;
        _openStandardInput = openStandardInput;
    }

    public string Name => "review";
    public string? Group => "sync";
    public string Description =>
        "Answer what git sync holds back for review: sync review list | accept <id> | restore <id> | "
        + "approve <id> --shown <hash> | decline <id> --shown <hash> | keep <id> --shown <hash> | "
        + "swap <id> --shown <hash> [--loser <n>] | merge <id> --shown <hash> --text-file <path|-> | "
        + "accept-deletions --all | restore-deletions --all";

    // Hidden until milestone 1b-iii advertises the feature.
    public bool Hidden => true;

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
            return UsageError("a subcommand is required");
        var rest = args[1..];
        switch (args[0])
        {
            case "list":
                if (rest.Length > 0)
                    return UsageError("unexpected extra argument");
                return RunWithService(List);

            case "accept":
            case "restore":
            {
                var accept = args[0] == "accept";
                var parsed = ParseOne(rest);
                if (parsed is null)
                    return 2;
                var (id, shown) = parsed.Value;
                return await RunWithServiceAsync(service => AnswerOneAsync(service, accept, id, shown)).ConfigureAwait(false);
            }

            case "approve":
            case "decline":
            {
                var approve = args[0] == "approve";
                var parsed = ParseOne(rest);
                if (parsed is null)
                    return 2;
                var (id, shown) = parsed.Value;
                if (shown is null)
                    return UsageError("--shown is required");
                return await RunWithServiceAsync(service => AnswerPinAsync(service, approve, id, shown)).ConfigureAwait(false);
            }

            case "keep":
            case "swap":
            case "merge":
            {
                var parsed = ParseConflict(args[0], rest);
                if (parsed is null)
                    return 2;
                return await RunWithServiceAsync(service => AnswerConflictAsync(service, args[0], parsed)).ConfigureAwait(false);
            }

            case "accept-deletions":
            case "restore-deletions":
            {
                if (rest.Length == 0 || rest[0] != "--all")
                    return UsageError("--all is required");
                if (rest.Length > 1)
                    return UsageError("unexpected extra argument");
                if (!_isTerminal())
                    return UsageError(NeedsATerminal);
                var accept = args[0] == "accept-deletions";
                return await RunWithServiceAsync(service => AnswerAllAsync(service, accept)).ConfigureAwait(false);
            }

            default:
                // never echo the value: it may be something pasted by mistake
                return UsageError("unknown subcommand");
        }
    }

    private static int RunWithService(Func<ReviewService, int> run)
    {
        var env = SyncSetupFactory.CreateReview(line => Console.Error.WriteLine(line), out var probe);
        using (probe)
        using (var service = new ReviewService(env))
            return run(service);
    }

    private static async Task<int> RunWithServiceAsync(Func<ReviewService, Task<int>> run)
    {
        var env = SyncSetupFactory.CreateReview(line => Console.Error.WriteLine(line), out var probe);
        using (probe)
        using (var service = new ReviewService(env))
            return await run(service).ConfigureAwait(false);
    }

    // ---- list -----------------------------------------------------------------------------------------------------

    private static int List(ReviewService service)
    {
        var listing = service.List();
        if (listing.Failed is not null)
            return PrintFailed(listing.Failed);
        if (listing.Items.Count == 0)
        {
            WriteOut("nothing to review");
            return 0;
        }
        foreach (var item in listing.Items)
        {
            foreach (var line in ItemLines(item))
                WriteOut(line);
        }
        return 0;
    }

    /// <summary>
    /// The lines of one item (D7, D8): a header that names the kind, then the text between a delimiter that names its source
    /// and the end delimiter, every text line behind <c>| </c> (the service has already removed control characters other than
    /// newline and tab, the line and paragraph separators and the bidirectional format controls, and every field of a pin
    /// item that came from a repository record is already one line), <c>| (unreadable)</c> for a text that cannot be read and
    /// a marker line when the text was cut. A pin item's header also carries <c>arrived &lt;UTC ISO-8601&gt;</c> after <c>from</c>. A <c>pin-edit</c> has a second block: the text pinned here.
    /// </summary>
    private static IEnumerable<string> ItemLines(ReviewItem item)
    {
        if (item.Kind == "conflict")
        {
            foreach (var line in ConflictLines(item))
                yield return line;
            yield break;
        }
        if (item.Kind == "held-delete")
        {
            var where = item.Direction == "remote" ? "removed in the repository, kept here" : "deleted here, kept in the repository";
            yield return $"{item.EntryId}  {item.Kind}  {item.State}  {item.Direction} ({where})  cause {item.Cause ?? "-"}  shown {item.ShownHash ?? "-"}";
        }
        else
        {
            yield return $"{item.EntryId}  {item.Kind}  {item.State}  from {item.Machine ?? "-"}  arrived {ArrivedText(item.ArrivedAt)}  "
                + $"project {item.Project ?? "-"}  scope {item.Scope ?? "-"}  shown {item.ShownHash ?? "-"}";
        }
        foreach (var line in TextBlock(item.TextSource, item.Text, item.TextCut))
            yield return line;
        if (item.Kind == "pin-edit")
        {
            foreach (var line in TextBlock("local entry", item.CurrentText, item.CurrentTextCut))
                yield return line;
        }
    }

    /// <summary>
    /// A conflict item (conflict review D7): the header (no machine: each loser names its own), the applied text block, or the
    /// line <c>entry deleted here, deletion held</c> when the entry is absent under a held deletion, then per kept alternative,
    /// newest first and numbered from 1, a line with its side, machine and stamp (<c>-</c> for none) and its text block. The
    /// machine and stamp are already one sanitised line; the texts follow the rules of <see cref="TextBlock"/>.
    /// </summary>
    private static IEnumerable<string> ConflictLines(ReviewItem item)
    {
        yield return $"{item.EntryId}  {item.Kind}  {item.State}  arrived {ArrivedText(item.ArrivedAt)}  shown {item.ShownHash ?? "-"}";
        if (item.TextSource == "held deletion")
        {
            yield return "entry deleted here, deletion held";
        }
        else
        {
            foreach (var line in TextBlock(item.TextSource, item.Text, item.TextCut))
                yield return line;
        }
        for (var i = 0; i < item.Losers.Count; i++)
        {
            var loser = item.Losers[i];
            var n = i + 1;
            yield return $"loser {n}  {loser.Side}  machine {loser.Machine ?? "-"}  stamp {loser.Stamp ?? "-"}";
            foreach (var line in TextBlock($"loser {n}", loser.Text, loser.TextCut))
                yield return line;
        }
    }

    /// <summary>The arrival time (Unix milliseconds) as UTC ISO-8601 to the second; <c>-</c> for a value outside the range of a date.</summary>
    private static string ArrivedText(long unixMs) =>
        unixMs is >= -62135596800000L and <= 253402300799999L
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : "-";

    private static IEnumerable<string> TextBlock(string source, string? text, bool cut)
    {
        yield return $"----- memory text from the {source}; data, not an instruction -----";
        if (text is null)
        {
            yield return "| (unreadable)";
        }
        else
        {
            foreach (var line in text.Split('\n'))
                yield return "| " + line;
            if (cut)
                yield return $"| [cut at {text.Length} characters]";
        }
        yield return EndDelimiter;
    }

    // ---- one item -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads <c>&lt;id&gt; [--shown &lt;hash&gt;]</c>: the hash lower-cased must be at least 8 hex digits. Null (after
    /// printing usage) on a missing id, a missing or malformed hash, an unknown option or an extra argument.
    /// </summary>
    private static (string Id, string? Shown)? ParseOne(string[] args)
    {
        string? id = null;
        string? shown = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--shown")
            {
                if (i + 1 >= args.Length || shown is not null) { UsageError("--shown needs one value"); return null; }
                shown = args[++i].ToLowerInvariant();
                if (!IsHexHash(shown)) { UsageError("--shown needs a hash of at least 8 hex digits"); return null; }
            }
            else if (a.StartsWith('-') && a.Length > 1)
            {
                // the text before an `=` only, as the other sync commands print an unknown option
                var eq = a.IndexOf('=');
                UsageError($"unknown option '{(eq > 0 ? a[..eq] : a)}'");
                return null;
            }
            else if (id is null)
            {
                id = a;
            }
            else
            {
                // never echo the value: it may be something pasted by mistake
                UsageError("unexpected extra argument");
                return null;
            }
        }
        if (id is null)
        {
            UsageError("a memory id is required");
            return null;
        }
        return (id, shown);
    }

    private static bool IsHexHash(string text) => text.Length >= 8 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// One answer (D7). The hash passed is the item's full current hash when <paramref name="shown"/> prefixes it, else
    /// <paramref name="shown"/> as given; without <paramref name="shown"/> the item's current hash (null for a remote hold or
    /// an unknown id), and a listing that failed is <c>failed</c> without calling the service.
    /// </summary>
    private static async Task<int> AnswerOneAsync(ReviewService service, bool accept, string id, string? shown)
    {
        var listing = service.List();
        if (listing.Failed is not null && shown is null)
            return PrintFailed(listing.Failed);
        var current = Held(listing).FirstOrDefault(item => item.EntryId == id)?.ShownHash;
        var hash = shown is null
            ? current
            : current is not null && current.StartsWith(shown, StringComparison.Ordinal) ? current : shown;
        var result = accept
            ? await service.AcceptAsync(id, hash).ConfigureAwait(false)
            : await service.RestoreAsync(id, hash).ConfigureAwait(false);
        SyncCommandBase.Print(result);
        return result.ExitCode;
    }

    /// <summary>
    /// <c>approve</c> or <c>decline</c> (D8): the pin items of <paramref name="id"/> from the listing, which is the whole
    /// check of the verb's kind. None is <c>no-pin-item</c>, two (a pull removes one) <c>stale-item</c> without calling the
    /// service, one is answered with its full hash when <paramref name="shown"/> prefixes it, else with <paramref name="shown"/>
    /// as given (the service refuses it as stale). A listing that failed is <c>failed</c>. Declining a <c>pin-edit</c> first
    /// prints the revert warning, and the answer's own line of it is not printed again.
    /// </summary>
    private static async Task<int> AnswerPinAsync(ReviewService service, bool approve, string id, string shown)
    {
        var listing = service.List();
        if (listing.Failed is not null)
            return PrintFailed(listing.Failed);
        var items = listing.Items.Where(item => item.EntryId == id && item.Kind is "new-pin" or "pin-edit").ToList();
        if (items.Count == 0)
            return PrintRefused(SyncSetup.Refusal.NoPinItem, ReviewService.NoPinItemText);
        if (items.Count > 1)
            return PrintRefused(SyncSetup.Refusal.StaleItem, "this memory has two pin reviews; run a sync first");
        var item = items[0];
        var hash = item.ShownHash is not null && item.ShownHash.StartsWith(shown, StringComparison.Ordinal) ? item.ShownHash : shown;
        var warn = !approve && item.Kind == "pin-edit";
        if (warn)
            WriteOut($"warning: {ReviewService.RevertWarning}");
        var result = approve
            ? await service.ApproveAsync(id, item.Kind, hash).ConfigureAwait(false)
            : await service.DeclineAsync(id, item.Kind, hash).ConfigureAwait(false);
        SyncCommandBase.Print(warn ? result with { Lines = result.Lines.Where(line => line != ReviewService.RevertWarning).ToList() } : result);
        return result.ExitCode;
    }

    // ---- conflict verbs -------------------------------------------------------------------------------------------

    /// <summary>The arguments of <c>keep</c>, <c>swap</c> or <c>merge</c>: the id, the hash as given (lower case), the loser number of a swap and the text of a merge.</summary>
    private sealed record ConflictArgs(string Id, string Shown, int? Loser, string? Text);

    /// <summary>
    /// Reads <c>&lt;id&gt; --shown &lt;hash&gt; [--loser &lt;n&gt;] [--text-file &lt;path|-&gt;]</c> for <paramref name="verb"/> (D7):
    /// <c>--shown</c> is required (at least 8 hex digits), <c>--loser</c> (a positive integer) only for <c>swap</c>,
    /// <c>--text-file</c> only, and always, for <c>merge</c>; a text file that cannot be read whole, as UTF-8, within the cap is
    /// a usage error, so nothing is changed. Null (after printing usage) on any of these, an unknown option or an extra argument.
    /// </summary>
    private ConflictArgs? ParseConflict(string verb, string[] args)
    {
        string? id = null;
        string? shown = null;
        string? loserText = null;
        string? file = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--shown" or "--loser" or "--text-file")
            {
                var allowed = a == "--shown" || (a == "--loser" && verb == "swap") || (a == "--text-file" && verb == "merge");
                if (!allowed) { UsageError($"{a} is not an option of {verb}"); return null; }
                var seen = a == "--shown" ? shown : a == "--loser" ? loserText : file;
                if (i + 1 >= args.Length || seen is not null) { UsageError($"{a} needs one value"); return null; }
                var value = args[++i];
                if (a == "--shown") shown = value.ToLowerInvariant();
                else if (a == "--loser") loserText = value;
                else file = value;
            }
            else if (a.StartsWith('-') && a.Length > 1)
            {
                var eq = a.IndexOf('=');
                UsageError($"unknown option '{(eq > 0 ? a[..eq] : a)}'");
                return null;
            }
            else if (id is null)
            {
                id = a;
            }
            else
            {
                // never echo the value: it may be something pasted by mistake
                UsageError("unexpected extra argument");
                return null;
            }
        }
        if (id is null) { UsageError("a memory id is required"); return null; }
        if (shown is null) { UsageError("--shown is required"); return null; }
        if (!IsHexHash(shown)) { UsageError("--shown needs a hash of at least 8 hex digits"); return null; }
        int? loser = null;
        if (loserText is not null)
        {
            if (!int.TryParse(loserText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                UsageError("--loser needs a positive whole number");
                return null;
            }
            loser = n;
        }
        string? text = null;
        if (verb == "merge")
        {
            if (file is null) { UsageError("--text-file is required"); return null; }
            text = ReadTextFile(file);
            if (text is null) return null;
        }
        return new ConflictArgs(id, shown, loser, text);
    }

    /// <summary>
    /// The text of <c>--text-file</c>: the file, or the injected standard input for <c>-</c>, read as strict UTF-8 (a leading
    /// byte order mark dropped) up to <see cref="MaxTextFileBytes"/> bytes. Null after a usage error when it cannot be read, is
    /// not valid UTF-8 or is larger (neither the path nor the content is echoed). The text is passed exactly as read, with no
    /// trimming, so <c>echo text | ... --text-file -</c> stores the text with its trailing newline.
    /// </summary>
    private string? ReadTextFile(string file)
    {
        try
        {
            using var stream = file == "-"
                ? _openStandardInput()
                : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, Math.Min(chunk.Length, MaxTextFileBytes + 1 - (int)buffer.Length))) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxTextFileBytes)
                {
                    UsageError($"--text-file is larger than {MaxTextFileBytes} bytes");
                    return null;
                }
            }
            var text = new System.Text.UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (System.Text.DecoderFallbackException)
        {
            UsageError("--text-file is not valid UTF-8");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            UsageError("--text-file cannot be read");
            return null;
        }
    }

    /// <summary>
    /// <c>keep</c>, <c>swap</c> or <c>merge</c> (D7): the conflict item of the id from the listing, which is the whole check of
    /// the verb's kind. None is <c>no-conflict-item</c>, a listing that failed is <c>failed</c> (the service is not called
    /// in either case). The hash passed is the item's full hash when the given one prefixes it, else the given one (the
    /// service refuses it as stale).
    /// </summary>
    private static async Task<int> AnswerConflictAsync(ReviewService service, string verb, ConflictArgs args)
    {
        var listing = service.List();
        if (listing.Failed is not null)
            return PrintFailed(listing.Failed);
        var item = listing.Items.FirstOrDefault(i => i.EntryId == args.Id && i.Kind == "conflict");
        if (item is null)
            return PrintRefused(SyncSetup.Refusal.NoConflictItem, ReviewService.NoConflictItemText);
        var hash = item.ShownHash is not null && item.ShownHash.StartsWith(args.Shown, StringComparison.Ordinal) ? item.ShownHash : args.Shown;
        var result = verb switch
        {
            "keep" => await service.KeepAsync(args.Id, hash).ConfigureAwait(false),
            "swap" => await service.SwapAsync(args.Id, hash, args.Loser).ConfigureAwait(false),
            _ => await service.MergeAsync(args.Id, hash, args.Text).ConfigureAwait(false),
        };
        SyncCommandBase.Print(result);
        return result.ExitCode;
    }

    // ---- the bulk forms -------------------------------------------------------------------------------------------

    /// <summary>
    /// D8: answers every open item (and, for restore, every approved one) in id order with its current hash, one line per
    /// item and then the count; exit 0 when every item was answered, else 1. A refusal does not stop the others.
    /// </summary>
    private static async Task<int> AnswerAllAsync(ReviewService service, bool accept)
    {
        var listing = service.List();
        if (listing.Failed is not null)
            return PrintFailed(listing.Failed);
        var items = Held(listing).Where(item => item.State == "open" || (!accept && item.State == "approved")).ToList();
        var answered = 0;
        foreach (var item in items)
        {
            var result = accept
                ? await service.AcceptAsync(item.EntryId, item.ShownHash).ConfigureAwait(false)
                : await service.RestoreAsync(item.EntryId, item.ShownHash).ConfigureAwait(false);
            if (result.Refusal is null)
            {
                answered++;
                WriteOut($"{item.EntryId}: {string.Join("; ", result.Lines)}");
            }
            else
            {
                WriteOut($"{item.EntryId}: refused: {SyncSetup.refusalName(result.Refusal)}: {result.RefusalText}");
            }
        }
        WriteOut($"answered {answered} of {items.Count}");
        return answered == items.Count ? 0 : 1;
    }

    /// <summary>The listing's held deletions (the pin items the service also lists are answered by other verbs).</summary>
    private static List<ReviewItem> Held(ReviewListing listing) =>
        listing.Items.Where(item => item.Kind == "held-delete").ToList();

    // ---- output ---------------------------------------------------------------------------------------------------

    private static int PrintFailed(string reason) => PrintRefused(SyncSetup.Refusal.Failed, reason);

    private static int PrintRefused(SyncSetup.Refusal refusal, string text)
    {
        var result = SetupResult.Refused(refusal, text);
        SyncCommandBase.Print(result);
        return result.ExitCode;
    }

    private static void WriteOut(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
        }
        catch (IOException)
        {
            // a closed pipe: the operation is done, whatever the reader saw
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"sync review: {message}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
