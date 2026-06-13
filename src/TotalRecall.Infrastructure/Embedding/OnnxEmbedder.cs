using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// ONNX-runtime backed text embedder. Lazily loads the ONNX model and the
/// WordPiece vocabulary on first use, then runs inference, CLS-pools (takes
/// the token-0 hidden state), and L2-normalizes the result.
///
/// Ports <c>src-ts/embedding/embedder.ts</c>.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const string ModelFileName = "model.onnx";
    private const string TokenizerFileName = "tokenizer.json";

    private readonly ModelManager _modelManager;
    private readonly string _modelName;
    private readonly object _loadLock = new();

    private InferenceSession? _session;
    private FSharpMap<string, int>? _vocab;
    private ModelSpec? _spec;
    private IReadOnlyList<string>? _inputNames;   // subset of input_ids/attention_mask/token_type_ids the graph declares
    private string? _outputName;                  // hidden-state output (prefers last_hidden_state)

    public OnnxEmbedder(ModelManager modelManager, string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelManager);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name must be provided.", nameof(modelName));
        }
        _modelManager = modelManager;
        _modelName = modelName;
    }

    /// <summary>
    /// True once the ONNX session and vocab have been loaded. Exposed for
    /// tests that verify laziness.
    /// </summary>
    public bool IsLoaded => _session is not null && _vocab is not null;

    /// <inheritdoc />
    public EmbedderDescriptor Descriptor
    {
        get
        {
            // Resolve dimensions/revision from the registry without loading
            // the ONNX session. If the model isn't in the registry we fall
            // back to a zero-dim descriptor; the fingerprint guard will
            // treat that as a mismatch against any previously-stamped
            // fingerprint and fail loudly rather than silently.
            var registry = _modelManager.Registry;
            if (registry.TryGetSpec(_modelName, out var spec) && spec is not null)
            {
                return new EmbedderDescriptor(
                    Provider: "local",
                    Model: _modelName,
                    Revision: spec.Revision ?? string.Empty,
                    Dimensions: spec.Dimensions);
            }
            return new EmbedderDescriptor("local", _modelName, string.Empty, 0);
        }
    }

    /// <inheritdoc />
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureLoaded();

        var tokenList = Tokenizer.tokenize(_vocab!, text);
        int[] tokenIds = ListModule.ToArray(tokenList);
        int seqLen = tokenIds.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });
        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1L;
            tokenTypeIds[0, i] = 0L;
        }

        var inputs = new List<NamedOnnxValue>(_inputNames!.Count);
        foreach (var name in _inputNames!)
        {
            inputs.Add(name switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(name, inputIds),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(name, attentionMask),
                "token_type_ids" => NamedOnnxValue.CreateFromTensor(name, tokenTypeIds),
                _ => throw new InvalidOperationException($"Unexpected ONNX input '{name}'"),
            });
        }

        using var results = _session!.Run(inputs, new[] { _outputName! });
        var hidden = results[0].AsTensor<float>(); // [1, seqLen, hiddenSize]
        var dims = hidden.Dimensions;
        if (dims.Length != 3)
        {
            throw new InvalidOperationException(
                $"ONNX output '{_outputName}' has rank {dims.Length}; CLS pooling requires a rank-3 " +
                "[batch,seq,hidden] hidden-state output. Wrong model?");
        }
        int hiddenSize = dims[2];
        if (hiddenSize != _spec!.Dimensions)
        {
            throw new InvalidOperationException(
                $"ONNX model produced {hiddenSize}-dim hidden states; expected {_spec.Dimensions}. Wrong model?");
        }

        var pooled = new float[hiddenSize];
        for (int d = 0; d < hiddenSize; d++)
        {
            pooled[d] = hidden[0, 0, d];
        }

        double sumSq = 0.0;
        for (int d = 0; d < hiddenSize; d++)
        {
            sumSq += pooled[d] * pooled[d];
        }
        float norm = (float)Math.Sqrt(sumSq);
        if (norm > 0f)
        {
            for (int d = 0; d < hiddenSize; d++)
            {
                pooled[d] /= norm;
            }
        }

        return pooled;
    }

    private void EnsureLoaded()
    {
        if (IsLoaded)
        {
            return;
        }

        lock (_loadLock)
        {
            if (IsLoaded)
            {
                return;
            }

            var modelDir = _modelManager.EnsureModelAvailable(_modelName);
            var spec = _modelManager.Registry.GetSpec(_modelName);

            var onnxPath = Path.Combine(modelDir, ModelFileName);
            var tokenizerPath = Path.Combine(modelDir, TokenizerFileName);

            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            var session = new InferenceSession(onnxPath, opts);

            var declared = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
            var wanted = new[] { "input_ids", "attention_mask", "token_type_ids" };
            var inputNames = wanted.Where(declared.Contains).ToArray();
            if (!inputNames.Contains("input_ids") || !inputNames.Contains("attention_mask"))
            {
                session.Dispose();
                throw new InvalidOperationException(
                    $"ONNX model inputs [{string.Join(",", declared)}] do not include the required input_ids + attention_mask.");
            }
            var outputName = session.OutputMetadata.ContainsKey("last_hidden_state")
                ? "last_hidden_state"
                : session.OutputMetadata.Keys.First();

            var vocab = LoadVocab(tokenizerPath);

            _session = session;
            _vocab = vocab;
            _spec = spec;
            _inputNames = inputNames;
            _outputName = outputName;
        }
    }

    private static FSharpMap<string, int> LoadVocab(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath))
        {
            throw new FileNotFoundException(
                $"tokenizer.json not found at {tokenizerJsonPath}",
                tokenizerJsonPath);
        }

        string json = File.ReadAllText(tokenizerJsonPath);

        TokenizerFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, TokenizerJsonContext.Default.TokenizerFileDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Failed to parse tokenizer.json at {tokenizerJsonPath}: {ex.Message}",
                ex);
        }

        if (dto?.Model?.Vocab is null)
        {
            throw new InvalidDataException(
                $"tokenizer.json at {tokenizerJsonPath} is missing model.vocab.");
        }

        // Build FSharpMap<string,int> from the dictionary. MapModule.OfSeq
        // consumes a sequence of System.Tuple<TKey, TValue>.
        var pairs = dto.Model.Vocab.Select(kv => new Tuple<string, int>(kv.Key, kv.Value));
        return MapModule.OfSeq(pairs);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _vocab = null;
        _spec = null;
        _inputNames = null;
        _outputName = null;
    }
}

// ---------- tokenizer.json DTOs + source-gen JSON context ----------

internal sealed class TokenizerFileDto
{
    [JsonPropertyName("model")]
    public TokenizerModelDto? Model { get; set; }
}

internal sealed class TokenizerModelDto
{
    [JsonPropertyName("vocab")]
    public Dictionary<string, int>? Vocab { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(TokenizerFileDto))]
internal sealed partial class TokenizerJsonContext : JsonSerializerContext
{
}
