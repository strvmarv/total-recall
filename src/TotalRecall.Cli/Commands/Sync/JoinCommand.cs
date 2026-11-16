using System.Threading.Tasks;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary><c>total-recall sync join &lt;repository&gt; [--branch &lt;name&gt;]</c> (hidden): adopts an existing sync repository.</summary>
public sealed class JoinCommand : SyncCommandBase
{
    public override string Name => "join";
    public override string Description => "Join an existing git sync repository: sync join <repository> [--branch <name>]";
    protected override string Usage => "Usage: total-recall sync join <repository> [--branch <name>]";
    protected override (string? Address, string? Branch)? Parse(string[] args) => ParseAddressAndBranch(args, addressIsUrlFlag: false);
    protected override Task<SetupResult> RunServiceAsync(SyncSetupService service, string? address, string? branch) =>
        service.JoinAsync(address!, branch);
}
