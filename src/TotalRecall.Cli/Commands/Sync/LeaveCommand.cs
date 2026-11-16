using System.Threading.Tasks;
using TotalRecall.Infrastructure.GitSync;

namespace TotalRecall.Cli.Commands.Sync;

/// <summary><c>total-recall sync leave</c> (hidden): turns git mode off; changes nothing else.</summary>
public sealed class LeaveCommand : SyncCommandBase
{
    public override string Name => "leave";
    public override string Description => "Turn git mode off (memories and the repository are untouched): sync leave";
    protected override string Usage => "Usage: total-recall sync leave";
    protected override (string? Address, string? Branch)? Parse(string[] args) => ParseNone(args);
    protected override Task<SetupResult> RunServiceAsync(SyncSetupService service, string? address, string? branch) =>
        service.LeaveAsync();
}
