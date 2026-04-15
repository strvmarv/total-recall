namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// AOT-safe JSON payload builders for sync queue entries.
/// Avoids reflection-based JsonSerializer in the AOT binary.
/// </summary>
internal static class SyncPayload
{
    public static string Upsert(string id, string content, string? scope = null)
    {
        if (scope is null)
            return $$"""{"id":"{{Escape(id)}}","content":"{{Escape(content)}}"}""";
        return $$"""{"id":"{{Escape(id)}}","content":"{{Escape(content)}}","scope":"{{Escape(scope)}}"}""";
    }

    public static string Delete(string id)
        => $$"""{"id":"{{Escape(id)}}"}""";

    private static string Escape(string s)
        => s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
}
