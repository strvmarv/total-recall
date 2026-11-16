using System.Threading.Tasks;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary><c>total-recall sync status</c> (hidden): what this machine knows about its sync setup. Read-only, no network, creates nothing.</summary>
public sealed class StatusCommand : SyncCommandBase
{
    public override string Name => "status";
    public override string Description => "Show this machine's git sync setup (read-only): sync status";
    protected override string Usage => "Usage: total-recall sync status";
    protected override (string? Address, string? Branch)? Parse(string[] args) => ParseNone(args);
    protected override Task<SetupResult> RunServiceAsync(SyncSetupService service, string? address, string? branch) =>
        service.StatusAsync();
}
