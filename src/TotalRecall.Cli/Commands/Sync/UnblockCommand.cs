using System.Threading.Tasks;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary>
/// <c>total-recall sync unblock &lt;id&gt;</c> (hidden; #161, spec decision D7): allows the current fields of a memory held
/// back because a secret was found in it, on this machine. The id travels in the address slot of <see cref="Parse"/>.
/// </summary>
public sealed class UnblockCommand : SyncCommandBase
{
    public override string Name => "unblock";
    public override string Description => "Allow a memory held back because a secret was found in it: sync unblock <id>";
    protected override string Usage => "Usage: total-recall sync unblock <id>";

    protected override (string? Address, string? Branch)? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            UsageError("a memory id is required");
            return null;
        }
        if (args.Length > 1)
        {
            // never echo the values: one may be something the user pasted by mistake
            UsageError("unexpected extra argument");
            return null;
        }
        var id = args[0];
        if (id.StartsWith('-') && id.Length > 1)
        {
            // the text before an `=` only, as the other sync commands print an unknown option
            var eq = id.IndexOf('=');
            UsageError($"unknown option '{(eq > 0 ? id[..eq] : id)}'");
            return null;
        }
        return (id, null);
    }

    protected override Task<SetupResult> RunServiceAsync(SyncSetupService service, string? address, string? branch) =>
        service.UnblockAsync(address!);
}
