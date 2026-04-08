// src/TotalRecall.Infrastructure/Eval/ConfigSnapshotStore.cs
//
// Plan 5 Task 5.3b — read/write seam on top of the config_snapshots
// table. Ports src-ts/config.ts:91-113 (createConfigSnapshot) and
// src-ts/tools/eval-tools.ts:85-96 (resolveSnapshotId).
//
// Dedup: the TS implementation hashes the config via sortKeysDeep -> sha256.
// The .NET port compares the raw configJson strings verbatim; semantic
// normalization (stable key order) is the caller's responsibility. This
// keeps the store AOT-trivial — no reflection, no object walking — and
// still satisfies the "don't insert a fresh row if nothing changed"
// invariant so long as callers use a stable serializer.

using System;
using System.Collections.Generic;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>One row of <c>config_snapshots</c>.</summary>
public sealed record ConfigSnapshotRow(
    string Id,
    string? Name,
    long Timestamp,
    string ConfigJson);

/// <summary>
/// Read/write seam over <c>config_snapshots</c>. Borrows a non-owning
/// <see cref="MsSqliteConnection"/>. See <see cref="ConfigSnapshotStore"/>
/// for the dedup contract.
/// </summary>
public interface IConfigSnapshotStore
{
    /// <summary>
    /// Insert a new snapshot row, or return the id of the latest row if
    /// its <c>config</c> column byte-equals <paramref name="configJson"/>.
    /// Callers wanting semantic dedup should normalize the JSON (stable
    /// key order) before calling.
    /// </summary>
    string CreateSnapshot(string configJson, string? name = null);

    ConfigSnapshotRow? GetLatest();
    ConfigSnapshotRow? GetById(string id);
    ConfigSnapshotRow? GetByName(string name);
    IReadOnlyList<ConfigSnapshotRow> ListRecent(int limit);

    /// <summary>
    /// Resolve a snapshot reference to a concrete id. Matches <c>eval-tools.ts</c>
    /// <c>resolveSnapshotId</c>: exact id match → id; <c>"latest"</c> → latest id;
    /// otherwise most-recent row with that name. Returns null if unresolved.
    /// </summary>
    string? ResolveRef(string nameOrId);
}

/// <inheritdoc cref="IConfigSnapshotStore"/>
public sealed class ConfigSnapshotStore : IConfigSnapshotStore
{
    private readonly MsSqliteConnection _conn;

    public ConfigSnapshotStore(MsSqliteConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
    }

    public string CreateSnapshot(string configJson, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(configJson);
        var latest = GetLatest();
        if (latest is not null && string.Equals(latest.ConfigJson, configJson, StringComparison.Ordinal))
        {
            return latest.Id;
        }

        var id = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO config_snapshots (id, name, timestamp, config) VALUES ($id, $name, $ts, $cfg)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$cfg", configJson);
        cmd.ExecuteNonQuery();

        return id;
    }

    public ConfigSnapshotRow? GetLatest()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, timestamp, config FROM config_snapshots ORDER BY timestamp DESC LIMIT 1";
        return ReadOne(cmd);
    }

    public ConfigSnapshotRow? GetById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, timestamp, config FROM config_snapshots WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadOne(cmd);
    }

    public ConfigSnapshotRow? GetByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, timestamp, config FROM config_snapshots WHERE name = $name ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        return ReadOne(cmd);
    }

    public IReadOnlyList<ConfigSnapshotRow> ListRecent(int limit)
    {
        if (limit <= 0) return Array.Empty<ConfigSnapshotRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, timestamp, config FROM config_snapshots ORDER BY timestamp DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<ConfigSnapshotRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ConfigSnapshotRow(
                Id: reader.GetString(0),
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                Timestamp: reader.GetInt64(2),
                ConfigJson: reader.GetString(3)));
        }
        return rows;
    }

    public string? ResolveRef(string nameOrId)
    {
        ArgumentNullException.ThrowIfNull(nameOrId);

        var byId = GetById(nameOrId);
        if (byId is not null) return byId.Id;

        if (string.Equals(nameOrId, "latest", StringComparison.Ordinal))
        {
            return GetLatest()?.Id;
        }

        return GetByName(nameOrId)?.Id;
    }

    private static ConfigSnapshotRow? ReadOne(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new ConfigSnapshotRow(
            Id: reader.GetString(0),
            Name: reader.IsDBNull(1) ? null : reader.GetString(1),
            Timestamp: reader.GetInt64(2),
            ConfigJson: reader.GetString(3));
    }
}
