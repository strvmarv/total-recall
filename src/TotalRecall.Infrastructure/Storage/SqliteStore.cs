// spike/dotnet/src/TotalRecall.Spike/Storage/SqliteStore.cs
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TotalRecall.Infrastructure.Storage;

public sealed record SearchHit(long Id, string Content, float Score);

public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        _conn.EnableExtensions(true);
        _conn.LoadExtension(ResolveVecExtensionPath());

        var schema = LoadSchema();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    public long Insert(string content, float[] embedding)
    {
        if (embedding.Length != 384)
            throw new ArgumentException("embedding must be 384 floats", nameof(embedding));

        using var tx = _conn.BeginTransaction();
        long id;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO entries (content, created) VALUES ($c, $ts) RETURNING id";
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            id = (long)cmd.ExecuteScalar()!;
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO vec_entries (id, embedding) VALUES ($id, $vec)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$vec", FloatsToBytes(embedding));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return id;
    }

    public IReadOnlyList<SearchHit> Search(float[] queryEmbedding, int topK)
    {
        if (queryEmbedding.Length != 384)
            throw new ArgumentException("queryEmbedding must be 384 floats", nameof(queryEmbedding));

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT v.id, e.content, v.distance
            FROM vec_entries v
            JOIN entries e ON e.id = v.id
            WHERE v.embedding MATCH $vec
              AND k = $k
            ORDER BY v.distance";
        cmd.Parameters.AddWithValue("$vec", FloatsToBytes(queryEmbedding));
        cmd.Parameters.AddWithValue("$k", topK);

        var hits = new List<SearchHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit(reader.GetInt64(0), reader.GetString(1), (float)reader.GetDouble(2)));
        }
        return hits;
    }

    public void Dispose() => _conn.Dispose();

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string LoadSchema()
    {
        var asm = typeof(SqliteStore).Assembly;
        var name = $"{asm.GetName().Name}.Storage.Schema.sql";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded schema not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ResolveVecExtensionPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtimes");
        var path = Path.Combine(dir, "vec0.so");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"sqlite-vec native library not found at {path}. Download from https://github.com/asg017/sqlite-vec/releases and place vec0.so there.",
                path);
        return path;
    }
}
