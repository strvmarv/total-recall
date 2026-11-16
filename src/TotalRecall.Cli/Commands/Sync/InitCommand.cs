using System.Threading.Tasks;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary><c>total-recall sync init --url &lt;repository&gt; [--branch &lt;name&gt;]</c> (hidden): starts a sync repository at an empty remote.</summary>
public sealed class InitCommand : SyncCommandBase
{
    public override string Name => "init";
    public override string Description => "Start a git sync repository at an empty remote: sync init --url <repository> [--branch <name>]";
    protected override string Usage => "Usage: total-recall sync init --url <repository> [--branch <name>]";
    protected override (string? Address, string? Branch)? Parse(string[] args) => ParseAddressAndBranch(args, addressIsUrlFlag: true);
    protected override Task<SetupResult> RunServiceAsync(SyncSetupService service, string? address, string? branch) =>
        service.InitAsync(address!, branch);
}
