// src/TotalRecall.Server/Handlers/SkillGetHandler.cs
//
// MCP handler for the `skill_get` tool. Accepts either an `id` (Guid) OR the
// natural-key triple (`name`, `scope`, `scopeId`), but not both. Returns the
// full SkillBundleDto or JSON `null` when not found.
//
// Read path: local SqliteSkillCache first; on miss, fall through to cortex
// via ISkillClient. Cortex errors are swallowed so a stale local cache
// returns null gracefully when cortex is unreachable.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Server.Handlers;

public sealed class SkillGetHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":      {"type":"string","description":"Skill GUID. Supply either id OR the name+scope+scopeId triple, not both."},
            "name":    {"type":"string","description":"Skill name (required when using natural-key lookup)"},
            "scope":   {"type":"string","description":"Skill scope (required when using natural-key lookup)"},
            "scopeId": {"type":"string","description":"Skill scope ID (required when using natural-key lookup)"}
          }
        }
        """).RootElement.Clone();

    private readonly ISkillCache? _cache;
    private readonly ISkillClient _client;

    public SkillGetHandler(ISkillCache? cache, ISkillClient client)
    {
        _cache = cache;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    // Backwards-compat ctor used by older tests that don't supply a cache.
    public SkillGetHandler(ISkillClient client) : this(null, client) { }

    public string Name => "skill_get";
    public string Description => "Fetch a single skill by id or by (name, scope, scopeId)";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("skill_get requires an object arguments");

        var args = arguments.Value;
        var idStr = ArgumentParsing.ReadOptionalString(args, "id");
        var name = ArgumentParsing.ReadOptionalString(args, "name");
        var scope = ArgumentParsing.ReadOptionalString(args, "scope");
        var scopeId = ArgumentParsing.ReadOptionalString(args, "scopeId");

        var haveNaturalKey = name is not null || scope is not null || scopeId is not null;

        SkillBundleDto? bundle = null;
        if (idStr is not null)
        {
            if (haveNaturalKey)
                throw new ArgumentException(
                    "skill_get: supply either id or name+scope+scopeId, not both");
            if (!Guid.TryParse(idStr, out var id))
                throw new ArgumentException("skill_get: id must be a GUID");

            if (_cache is not null)
            {
                var cached = await _cache.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    bundle = ToBundle(cached);
                    await TryRecordAsync(cached.Id, ct).ConfigureAwait(false);
                }
            }
            if (bundle is null)
            {
                try { bundle = await _client.GetByIdAsync(id, ct).ConfigureAwait(false); }
                catch (CortexUnreachableException) { /* swallow */ }
            }
        }
        else if (name is not null && scope is not null && scopeId is not null)
        {
            if (_cache is not null)
            {
                var cached = await _cache.GetByNaturalKeyAsync(name, scope, scopeId, ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    bundle = ToBundle(cached);
                    await TryRecordAsync(cached.Id, ct).ConfigureAwait(false);
                }
            }
            if (bundle is null)
            {
                try { bundle = await _client.GetByNaturalKeyAsync(name, scope, scopeId, ct).ConfigureAwait(false); }
                catch (CortexUnreachableException) { /* swallow */ }
            }
        }
        else
        {
            throw new ArgumentException(
                "skill_get: supply either id or the full name+scope+scopeId triple");
        }

        // When bundle is null STJ emits the literal JSON "null" via the
        // non-nullable JsonTypeInfo<SkillBundleDto>; tolerate the nullability
        // mismatch with a bang since Serialize handles null values correctly.
        var text = JsonSerializer.Serialize(bundle!, JsonContext.Default.SkillBundleDto);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }

    private async Task TryRecordAsync(Guid id, CancellationToken ct)
    {
        if (_cache is null) return;
        try
        {
            await _cache.RecordInvocationAsync(
                id, host: null, sessionId: null,
                occurredAt: DateTime.UtcNow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort — must not block the agent */ }
    }

    private static SkillBundleDto ToBundle(CachedSkill s) =>
        new(
            Skill: new SkillDto(
                Id: s.Id,
                Name: s.Name,
                Description: s.Description,
                Scope: s.Scope,
                ScopeId: s.ScopeId,
                Tags: s.Tags,
                Version: s.Version,
                Source: s.Source,
                UpdatedAt: new DateTimeOffset(DateTime.SpecifyKind(s.UpdatedAt, DateTimeKind.Utc)),
                CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(s.UpdatedAt, DateTimeKind.Utc))),
            Content: s.Content,
            FrontmatterJson: s.FrontmatterJson ?? "{}",
            Files: Array.Empty<SkillFileDto>());
}
