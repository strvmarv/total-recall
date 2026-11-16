using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// Decides, by reading the database file without changing it, whether the setup commands may open it with the schema
/// runner. They must not in the states <c>AutoMigrationGuard</c> (in the Server project, which the CLI does not
/// reference) would still act on, or in which migration 5 would sweep rows: a TypeScript-format database, or one with
/// a <c>.ts-backup</c> beside it and no completion marker. The table is decision 16 of the 1a-3 spec.
/// </summary>
public static class LegacyDatabaseProbe
{
    public const string RefusalText = "this database has not been migrated yet: start total-recall once first";

    /// <summary>The suffix of the guard's backup file (the guard's own constant lives in the Server project).</summary>
    public const string BackupSuffix = ".ts-backup";

    /// <summary>Null to proceed, or the refusal text. Throws for a file that is not a SQLite database, or a pending write-ahead log.</summary>
    public static string? Check(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var backup = File.Exists(dbPath + BackupSuffix);

        var open = ReadOnlyDatabase.Open(dbPath);
        if (open.Unknown is not null)
            throw new InvalidOperationException(open.Unknown);
        if (open.Connection is null)
            return backup ? RefusalText : null; // no database file

        using var conn = open.Connection;
        if (Scalar(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table'") == 0)
            return backup ? RefusalText : null; // an empty file

        if (Scalar(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '_schema_version'") == 0)
            return RefusalText;
        if (Scalar(conn, "SELECT COALESCE(MAX(version), 0) FROM _schema_version") < 5)
            return RefusalText;

        if (!backup)
            return null;
        return MarkerPresent(conn) ? null : RefusalText;
    }

    private static bool MarkerPresent(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        if (Scalar(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '_meta'") == 0)
            return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", MigrationRunner.MigrationCompleteMarkerKey);
        return cmd.ExecuteScalar() is string { Length: > 0 };
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
