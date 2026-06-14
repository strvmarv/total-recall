// src/TotalRecall.Server/CatalogDumper.cs
//
// Emits the authoritative tools/list JSON for LOCAL (sqlite) mode, used at
// build/release to (re)generate the committed catalog.json that the Node shim
// serves before the engine is up. Hermetic: forces a clean temp data dir and
// clears backend-selecting env vars so it always resolves to local sqlite and
// never touches the user's DB, cortex, or postgres.

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TotalRecall.Server;

public static class CatalogDumper
{
    /// <summary>Builds the local-mode registry against a throwaway temp DB and
    /// returns the serialized tools/list result ({ "tools": [...] }).</summary>
    public static string DumpLocalCatalogJson()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "tr-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        var dbPath = Path.Combine(tempHome, "catalog-dump.db");

        // Snapshot the env vars we mutate so we can restore them. Without this,
        // calling this from an in-process test runner (xunit shares one process
        // across test classes) would leak TOTAL_RECALL_HOME etc. into every
        // subsequently-running test and break their isolation.
        var prevHome = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        var prevDbPath = Environment.GetEnvironmentVariable("TOTAL_RECALL_DB_PATH");
        var prevCortexUrl = Environment.GetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL");
        var prevCortexPat = Environment.GetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT");

        // Force local sqlite: empty home (no config.toml) + cleared backend env vars.
        // TOTAL_RECALL_HOME with no config.toml causes LoadEffectiveConfig to return
        // defaults → configuredMode = "local" → OpenSqlite is used.
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", tempHome);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", dbPath);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL", "");
        Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT", "");

        ServerCompositionHandles? handles = null;
        try
        {
            handles = ServerComposition.OpenProduction(dbPath);
            // ListTools() already returns a ToolSpec[] behind IReadOnlyList; reuse
            // it directly, falling back to a materialize only if that ever changes.
            var specs = handles.Registry.ListTools();
            var arr = specs as ToolSpec[] ?? System.Linq.Enumerable.ToArray(specs);
            var result = new ToolsListResult { Tools = arr };
            // Serialize via source-gen context (compact), then re-indent using
            // Utf8JsonWriter so it stays AOT/NativeAOT-safe (no reflection).
            var compact = JsonSerializer.Serialize(result, JsonContext.Default.ToolsListResult);
            return Reindent(compact);
        }
        finally
        {
            handles?.Dispose();
            // Restore env to its pre-call state (setting null removes the var).
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prevHome);
            Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", prevDbPath);
            Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL", prevCortexUrl);
            Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT", prevCortexPat);
            try { Directory.Delete(tempHome, recursive: true); } catch { /* best-effort */ }
        }
    }

    // JsonContext is compact (AOT, no indentation option). Re-parse and re-emit
    // via Utf8JsonWriter with Indented=true for a stable, reviewable committed
    // file — AOT-safe: uses JsonElement.WriteTo, no reflection serializer.
    private static string Reindent(string compactJson)
    {
        using var doc = JsonDocument.Parse(compactJson);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            doc.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
