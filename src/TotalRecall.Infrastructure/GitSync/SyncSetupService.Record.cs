using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class SyncSetupService
{
    private async Task FaultPointAsync(string name)
    {
        if (env.Fault is not null)
            await env.Fault(name).ConfigureAwait(false);
    }

    /// <summary>
    /// The record of decision 10, in the order that is the safety: place the clone, then the database in one
    /// transaction, then the config. A step that fails to place the clone is a <c>note:</c> line, not a failure; a failure
    /// of the database or the config throws and the caller reports it (running the command again repairs it).
    /// Returns the notes to print.
    /// </summary>
    private async Task<IReadOnlyList<string>> RecordAsync(Prepared p, RecordPlan plan, CancellationToken ct)
    {
        var notes = new List<string>();

        _phase = "placing the clone";
        await PlaceCloneAsync(p, plan, notes, ct).ConfigureAwait(false);
        await FaultPointAsync("after-placement").ConfigureAwait(false);

        _phase = "recording the identity";
        var storedMachine = p.Meta.GetMeta(SyncIdentity.MetaKeys.Machine);
        var storedHost = p.Meta.GetMeta(SyncIdentity.MetaKeys.Host);
        string? machine = null;
        if (plan.Kind != RecordKind.JoinSame
            || !SyncSetup.keepMachineId(
                string.IsNullOrEmpty(storedMachine) ? Microsoft.FSharp.Core.FSharpOption<string>.None : Microsoft.FSharp.Core.FSharpOption<string>.Some(storedMachine),
                string.IsNullOrEmpty(storedHost) ? Microsoft.FSharp.Core.FSharpOption<string>.None : Microsoft.FSharp.Core.FSharpOption<string>.Some(storedHost),
                p.Fingerprint))
        {
            machine = SyncSetup.newMachineId(env.Random(4));
        }

        using (var tx = p.Connection.BeginTransaction())
        {
            if (plan.Kind != RecordKind.JoinSame)
                GitSyncStateStore.ClearForNewRepository(p.Connection, tx);
            GitSyncStateStore.WriteIdentity(
                p.Connection, tx, p.Address, plan.Kind == RecordKind.JoinSame ? null : plan.RepoId, p.Fingerprint, machine);
            tx.Commit();
        }
        await FaultPointAsync("after-database").ConfigureAwait(false);

        WriteConfig(ConfigKeys(p.Address, p.Branch));
        return notes;
    }

    /// <summary>
    /// Decision 10.1. (a) A different repository, with another address stored and its clone present: that clone is moved
    /// aside (<c>left</c>). (b) The same repository under another spelling: whatever is at the new address's clone
    /// directory is moved aside (<c>superseded</c>), the old clone is renamed to the new place and re-pointed, so the commit
    /// and the protective ref survive. A failure of any step is a note.
    /// </summary>
    private async Task PlaceCloneAsync(Prepared p, RecordPlan plan, List<string> notes, CancellationToken ct)
    {
        var storedRemote = FSharpOptionOrNull(p.Stored.Remote);
        if (storedRemote is null || string.Equals(storedRemote, p.Address, StringComparison.Ordinal))
            return;

        string oldClone;
        try
        {
            oldClone = GitSyncPaths.For(env.DatabasePath, storedRemote, env.CaseInsensitive).CloneDirectory;
            if (!Directory.Exists(oldClone))
                return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"note: the earlier clone could not be located: {ex.Message}");
            return;
        }

        if (!plan.SameRepository)
        {
            try
            {
                var moved = CloneSideline.Move(oldClone, "left", env.Clock(), env.Move);
                notes.Add($"note: the clone of the earlier repository was kept at {moved}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"note: the clone of the earlier repository could not be moved aside: {ex.Message}");
            }
            return;
        }

        var newClone = p.Paths.CloneDirectory;
        try
        {
            if (Directory.Exists(newClone))
            {
                var moved = CloneSideline.Move(newClone, "superseded", env.Clock(), env.Move);
                notes.Add($"note: a clone at the new address was kept at {moved}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(newClone)!);
            (env.Move ?? Directory.Move)(oldClone, newClone);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"note: the earlier clone could not be moved to the new address: {ex.Message}");
            return;
        }

        var origin = await p.Client.SetOriginAsync(p.Address, ct).ConfigureAwait(false);
        if (!origin.IsOk)
            notes.Add("note: the moved clone could not be re-pointed at the new address; `sync join` again repairs it");
    }

    private static string? FSharpOptionOrNull(Microsoft.FSharp.Core.FSharpOption<string> option) =>
        Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(option) ? option.Value : null;
}
