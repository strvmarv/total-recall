// Shared by the four hidden `sync` commands: argument parsing and printing only. Everything else is SyncSetupService.
//
// Output (spec decision 3): results go to standard output; standard error carries notices and git's progress as they
// happen, then one `refused: <name>: <text>` line, then any `note:` lines. Exit 0 (done), 1 (refused or failed), 2 (usage).
// A failing write to standard output (a closed pipe) never changes the exit code.

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

public abstract class SyncCommandBase : ICliCommand
{
    public abstract string Name { get; }
    public string? Group => "sync";
    public abstract string Description { get; }

    // Hidden until milestone 1b-iii advertises the feature.
    public bool Hidden => true;

    protected abstract string Usage { get; }

    /// <summary>The parsed arguments, or null after a usage error was printed.</summary>
    protected abstract (string? Address, string? Branch)? Parse(string[] args);

    protected abstract Task<SetupResult> RunServiceAsync(SyncSetupService service, string? address, string? branch);

    public async Task<int> RunAsync(string[] args)
    {
        var parsed = Parse(args);
        if (parsed is null)
            return 2;

        var env = SyncSetupFactory.Create(line => Console.Error.WriteLine(line), out var probe);
        using (probe)
        {
            var result = await RunServiceAsync(new SyncSetupService(env), parsed.Value.Address, parsed.Value.Branch).ConfigureAwait(false);
            Print(result);
            return result.ExitCode;
        }
    }

    private static void Print(SetupResult result)
    {
        foreach (var line in result.Lines)
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
        if (result.Refusal is not null)
            Console.Error.WriteLine($"refused: {SyncSetup.refusalName(result.Refusal)}: {result.RefusalText}");
        foreach (var note in result.Notes)
            Console.Error.WriteLine(note);
    }

    protected int UsageError(string message)
    {
        Console.Error.WriteLine($"sync {Name}: {message}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    /// <summary>Reads <c>--branch &lt;name&gt;</c> and the allowed address form; null (after printing usage) on any other argument.</summary>
    protected (string? Address, string? Branch)? ParseAddressAndBranch(string[] args, bool addressIsUrlFlag)
    {
        string? address = null;
        string? branch = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--branch")
            {
                if (i + 1 >= args.Length || branch is not null) { UsageError("--branch needs one value"); return null; }
                branch = args[++i];
            }
            else if (addressIsUrlFlag && a == "--url")
            {
                if (i + 1 >= args.Length || address is not null) { UsageError("--url needs one value"); return null; }
                address = args[++i];
            }
            else if (a.StartsWith('-') && a.Length > 1)
            {
                UsageError($"unknown argument '{a}'");
                return null;
            }
            else if (!addressIsUrlFlag && address is null)
            {
                address = a;
            }
            else
            {
                UsageError($"unexpected argument '{a}'");
                return null;
            }
        }
        if (address is null)
        {
            UsageError(addressIsUrlFlag ? "--url <repository> is required" : "a repository address is required");
            return null;
        }
        return (address, branch);
    }

    /// <summary>For the commands that take no arguments.</summary>
    protected (string? Address, string? Branch)? ParseNone(string[] args)
    {
        if (args.Length > 0)
        {
            UsageError($"unexpected argument '{args[0]}'");
            return null;
        }
        return (null, null);
    }
}
