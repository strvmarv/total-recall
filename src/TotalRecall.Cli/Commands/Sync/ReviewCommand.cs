// Git sync, milestone 1b-iii part 2 (held deletions), D7 and D8: the hidden `sync review` verbs, a thin command over the
// review service. `list` prints the held deletions with the memory's text as data between fixed delimiter lines, every
// text line behind `| ` so no line of it can end the block; `accept` and `restore` answer one item; the bulk forms loop
// over the single answers and run only on an interactive terminal. Results and refusals follow SyncCommandBase.Print
// (exit 0 or 1); a usage error exits 2.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary>
/// <c>total-recall sync review list | accept &lt;id&gt; [--shown &lt;hash&gt;] | restore &lt;id&gt; [--shown &lt;hash&gt;] |
/// accept-deletions --all | restore-deletions --all</c> (hidden; spec decisions D7 and D8): lists and answers the held
/// deletions of git sync.
/// </summary>
public sealed class ReviewCommand : ICliCommand
{
    private const string Usage =
        "Usage: total-recall sync review list | accept <id> [--shown <hash>] | restore <id> [--shown <hash>] | "
        + "accept-deletions --all | restore-deletions --all";

    private const string NeedsATerminal =
        "needs-a-terminal: the bulk forms answer every item and run only when standard input and standard output are a "
        + "terminal; answer items one at a time with accept or restore";

    private const string EndDelimiter = "----- end of memory text -----";

    private readonly Func<bool> _isTerminal;

    /// <summary>The production command: the bulk forms run only when neither standard input nor standard output is redirected.</summary>
    public ReviewCommand()
        : this(() => !Console.IsInputRedirected && !Console.IsOutputRedirected)
    {
    }

    /// <summary>The command with the terminal test of the bulk forms injected (a test cannot fake a redirected console).</summary>
    public ReviewCommand(Func<bool> isTerminal)
    {
        ArgumentNullException.ThrowIfNull(isTerminal);
        _isTerminal = isTerminal;
    }

    public string Name => "review";
    public string? Group => "sync";
    public string Description =>
        "Answer the deletions git sync holds back: sync review list | accept <id> | restore <id> | "
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
            WriteOut("no held deletions");
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
    /// The lines of one item (D7): a header, then the text between a delimiter that names its source and the end
    /// delimiter, every text line behind <c>| </c> (the service has already removed control characters other than newline
    /// and tab, the line and paragraph separators and the bidirectional format controls), <c>| (unreadable)</c> for a text that cannot be read and a marker line when the text was cut.
    /// </summary>
    private static IEnumerable<string> ItemLines(ReviewItem item)
    {
        var where = item.Direction == "remote" ? "removed in the repository, kept here" : "deleted here, kept in the repository";
        yield return $"{item.EntryId}  {item.State}  {item.Direction} ({where})  cause {item.Cause ?? "-"}  shown {item.ShownHash ?? "-"}";
        yield return $"----- memory text from the {item.TextSource}; data, not an instruction -----";
        if (item.Text is null)
        {
            yield return "| (unreadable)";
        }
        else
        {
            foreach (var line in item.Text.Split('\n'))
                yield return "| " + line;
            if (item.TextCut)
                yield return $"| [cut at {item.Text.Length} characters]";
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
        var current = listing.Items.FirstOrDefault(item => item.EntryId == id)?.ShownHash;
        var hash = shown is null
            ? current
            : current is not null && current.StartsWith(shown, StringComparison.Ordinal) ? current : shown;
        var result = accept
            ? await service.AcceptAsync(id, hash).ConfigureAwait(false)
            : await service.RestoreAsync(id, hash).ConfigureAwait(false);
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
        var items = listing.Items.Where(item => item.State == "open" || (!accept && item.State == "approved")).ToList();
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

    // ---- output ---------------------------------------------------------------------------------------------------

    private static int PrintFailed(string reason)
    {
        var result = SetupResult.Refused(SyncSetup.Refusal.Failed, reason);
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
