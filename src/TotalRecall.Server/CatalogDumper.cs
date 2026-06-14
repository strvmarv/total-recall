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
            var specs = handles.Registry.ListTools();
            var arr = new ToolSpec[specs.Count];
            for (var i = 0; i < specs.Count; i++) arr[i] = specs[i];
            var result = new ToolsListResult { Tools = arr };
            // Serialize via source-gen context (compact), then re-indent using
            // Utf8JsonWriter so it stays AOT/NativeAOT-safe (no reflection).
            var compact = JsonSerializer.Serialize(result, JsonContext.Default.ToolsListResult);
            return Reindent(compact);
        }
        finally
        {
            handles?.Dispose();
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
