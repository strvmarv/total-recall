using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Tests.Commands;

[Collection("ConsoleCapture")]
public sealed class ReindexEmbeddingsCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public ReindexEmbeddingsCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    [Fact]
    public async Task Reindex_ReEmbedsSeededEntries_ReturnsZero_AndReportsCount()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "tr-cli-reindex-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Seed two memories with placeholder vectors, then close the
            // connection (and drain the pool) so the command can open the file.
            using (var conn = SqliteConnection.Open(dbPath))
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                _ = new VectorSearch(conn);

                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("fact one"), new float[384]);
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("fact two"), new float[384]);
            }
            MsSqliteConnection.ClearAllPools();

            var rc = await new ReindexEmbeddingsCommand().RunAsync(new[] { "--db", dbPath });

            Assert.Equal(0, rc);
            Assert.Contains("re-embedded 2 entries", _outWriter.ToString());

            // Prove the reindex actually rewrote the vectors rather than merely
            // returning a count. The seed vectors were all-zero (new float[384]);
            // after running the real bge embedder the stored vec0 rows must be
            // non-zero. The `embedding` column is raw little-endian float32 bytes
            // (384 * 4 = 1536 bytes); assert at least one byte is non-zero.
            MsSqliteConnection.ClearAllPools();
            using (var verify = SqliteConnection.Open(dbPath))
            {
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT embedding FROM warm_memories_vec";
                using var reader = cmd.ExecuteReader();
                int rowsSeen = 0;
                while (reader.Read())
                {
                    rowsSeen++;
                    var bytes = (byte[])reader["embedding"];
                    Assert.NotEmpty(bytes);
                    Assert.Contains(bytes, b => b != 0);
                }
                Assert.Equal(2, rowsSeen);
            }
        }
        finally
        {
            // The sqlite-vec native handle + Microsoft.Data.Sqlite pooling can
            // keep the file mapped briefly past Dispose on Windows; drain the
            // pool, then delete the db (and WAL/SHM sidecars) best-effort so a
            // transient lock never fails an otherwise-passing test.
            MsSqliteConnection.ClearAllPools();
            foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (IOException) { }
            }
        }
    }

    private static async Task<(int Code, string Err)> RunWithStorage(string storageToml, StringWriter err)
    {
        var home = Path.Combine(Path.GetTempPath(), "tr-reindex-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var previous = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            File.WriteAllText(Path.Combine(home, "config.toml"), storageToml);
            var missing = Path.Combine(home, "does-not-exist.db");

            var code = await new ReindexEmbeddingsCommand().RunAsync(new[] { "--db", missing });

            return (code, err.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", previous);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task GitMode_WithAConnectionString_IsALocalStore_NotPostgres()
    {
        // Before the edit an unknown mode with a connection string counted as Postgres and was refused here.
        var (code, err) = await RunWithStorage("[storage]\nmode = \"git\"\nconnection_string = \"Host=x\"\n", _errWriter);

        Assert.Equal(1, code);
        Assert.Contains("database not found", err);
        Assert.DoesNotContain("postgres backend not yet supported", err);
    }

    [Fact]
    public async Task PostgresMode_IsStillRefused()
    {
        var (code, err) = await RunWithStorage("[storage]\nmode = \"postgres\"\nconnection_string = \"Host=x\"\n", _errWriter);

        Assert.Equal(1, code);
        Assert.Contains("postgres backend not yet supported", err);
    }
}
