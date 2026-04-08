namespace TotalRecall.Server;

/// <summary>
/// Minimal seam for the TS→.NET one-time data migration.
/// Plan 5 implements a concrete <c>MigrateCommand</c>. Plan 4's
/// <see cref="AutoMigrationGuard"/> depends only on this interface so it can
/// be developed and tested in isolation.
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
