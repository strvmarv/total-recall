using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Loads and caches the model registry (<c>models/registry.json</c>).
/// AOT-safe: uses a source-generated <see cref="JsonSerializerContext"/> so no
/// reflection-based deserialization is required.
/// </summary>
public sealed class ModelRegistry
{
    public const int SupportedVersion = 1;

    private readonly IReadOnlyDictionary<string, ModelSpec> _models;

    public int Version { get; }

    public IReadOnlyCollection<string> ModelNames => (IReadOnlyCollection<string>)_models.Keys;

    private ModelRegistry(int version, IReadOnlyDictionary<string, ModelSpec> models)
    {
        Version = version;
        _models = models;
    }

    /// <summary>
    /// Reads and parses a registry JSON file. Throws on missing file, malformed
    /// JSON, or unsupported version.
    /// </summary>
    public static ModelRegistry LoadFromFile(string registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
        {
            throw new ArgumentException("Registry path must be provided.", nameof(registryPath));
        }

        if (!File.Exists(registryPath))
        {
            throw new FileNotFoundException(
                $"Model registry file not found: {registryPath}",
                registryPath);
        }

        string json = File.ReadAllText(registryPath);

        RegistryFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ModelRegistryJsonContext.Default.RegistryFileDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Failed to parse model registry at {registryPath}: {ex.Message}",
                ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException(
                $"Model registry at {registryPath} parsed to null.");
        }

        if (dto.Version != SupportedVersion)
        {
            throw new InvalidDataException(
                $"Unsupported model registry version {dto.Version} in {registryPath}. " +
                $"Expected version {SupportedVersion}.");
        }

        var models = new Dictionary<string, ModelSpec>(StringComparer.Ordinal);
        foreach (var kvp in dto.Models)
        {
            var name = kvp.Key;
            var specDto = kvp.Value ?? throw new InvalidDataException(
                $"Model registry entry '{name}' is null in {registryPath}.");

            var files = new Dictionary<string, string>(specDto.Files, StringComparer.Ordinal);

            models[name] = new ModelSpec(
                Name: name,
                Dimensions: specDto.Dimensions,
                Sha256: specDto.Sha256,
                SizeBytes: specDto.SizeBytes,
                Revision: specDto.Revision,
                Files: files);
        }

        return new ModelRegistry(dto.Version, models);
    }

    /// <summary>
    /// Looks up a model by name. Throws <see cref="KeyNotFoundException"/> with
    /// the list of available models if the name is unknown.
    /// </summary>
    public ModelSpec GetSpec(string name)
    {
        if (_models.TryGetValue(name, out var spec))
        {
            return spec;
        }

        var available = _models.Count == 0
            ? "(none)"
            : string.Join(", ", _models.Keys.OrderBy(n => n, StringComparer.Ordinal));
        throw new KeyNotFoundException(
            $"Model '{name}' not found in registry. Available models: {available}.");
    }

    /// <summary>
    /// Returns <c>true</c> if the given model name is known to the registry.
    /// </summary>
    public bool TryGetSpec(string name, out ModelSpec? spec)
    {
        if (_models.TryGetValue(name, out var found))
        {
            spec = found;
            return true;
        }

        spec = null;
        return false;
    }
}

// ---------- DTOs + source-generated JSON context (internal) ----------

internal sealed class RegistryFileDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("models")]
    public Dictionary<string, ModelSpecDto> Models { get; set; } = new();
}

internal sealed class ModelSpecDto
{
    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(RegistryFileDto))]
internal sealed partial class ModelRegistryJsonContext : JsonSerializerContext
{
}
