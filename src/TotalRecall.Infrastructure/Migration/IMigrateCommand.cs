namespace TotalRecall.Infrastructure.Migration;

/// <summary>
/// Seam for the TS→.NET one-time data migration. Lives in Infrastructure so
/// both <c>TotalRecall.Server.AutoMigrationGuard</c> (startup) and
/// <c>TotalRecall.Cli</c>'s <c>migrate</c> subcommand (explicit invocation) can
/// reference it without introducing a Server↔Cli circular dependency.
/// </summary>
public interface IMigrateCommand
{
    Task<MigrationResult> MigrateAsync(
        string sourceDbPath,
        string targetDbPath,
        CancellationToken ct);
}

public sealed record MigrationResult(
    bool Success,
    int EntriesMigrated,
    string? ErrorMessage);
