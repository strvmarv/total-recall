// REFERENCE FILE — kept from the .NET viability spike as a reference for the
// F# tokenizer port in Plan 2. Not part of the production code path. The
// canonical tokenizer lives in TotalRecall.Core (F#) and is wrapped by an
// adapter in TotalRecall.Infrastructure.Embedding when ready.
//
// To be deleted at the end of Plan 2 once the F# tokenizer is in place.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Wraps Microsoft.ML.Tokenizers.BertTokenizer with configuration that
/// reproduces the hand-rolled WordPiece tokenizer in src/embedding/tokenizer.ts.
///
/// Special tokens (hardcoded on the TS side, verified against tokenizer_config.json):
///   CLS = 101, SEP = 102, UNK = 100
///
/// Truncation: MAX_SEQ_LEN = 512. The TS code stops adding ids when
/// ids.length >= MAX_SEQ_LEN-1 then appends SEP. We mirror that exactly.
/// </summary>
internal sealed class BertTokenizerReference
{
    private const int MaxSeqLen = 512;
    private const int ClsId = 101;
    private const int SepId = 102;

    private readonly Microsoft.ML.Tokenizers.BertTokenizer _inner;

    public BertTokenizerReference(string modelDir)
    {
        var vocabPath = Path.Combine(modelDir, "vocab.txt");
        if (!File.Exists(vocabPath))
            ExtractVocabTxtFromTokenizerJson(modelDir, vocabPath);

        // API in Microsoft.ML.Tokenizers 2.0.0:
        //   BertTokenizer.Create(string vocabFilePath, BertOptions options)
        _inner = Microsoft.ML.Tokenizers.BertTokenizer.Create(
            vocabPath,
            new BertOptions
            {
                LowerCaseBeforeTokenization = true,
                ApplyBasicTokenization = true,
                IndividuallyTokenizeCjk = true,
            });
    }

    public int[] Encode(string text)
    {
        // Use the addSpecialTokens=true overload so CLS/SEP are included.
        var raw = _inner.EncodeToIds(text, addSpecialTokens: true, considerPreTokenization: true, considerNormalization: true);
        var list = new List<int>(raw);

        // Defensive: ensure CLS at start and SEP at end in case the inner
        // tokenizer doesn't add them (e.g. empty string or unusual config).
        if (list.Count == 0 || list[0] != ClsId)
            list.Insert(0, ClsId);
        if (list[^1] != SepId)
            list.Add(SepId);

        // Truncate to MAX_SEQ_LEN, mirroring the TS logic:
        //   keep ids[0..MaxSeqLen-2], then force SEP at position MaxSeqLen-1.
        if (list.Count > MaxSeqLen)
        {
            list.RemoveRange(MaxSeqLen - 1, list.Count - (MaxSeqLen - 1));
            list.Add(SepId);
        }

        return list.ToArray();
    }

    private static void ExtractVocabTxtFromTokenizerJson(string modelDir, string vocabPath)
    {
        var tokenizerJsonPath = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerJsonPath))
            throw new FileNotFoundException(
                $"Neither vocab.txt nor tokenizer.json found in {modelDir}",
                tokenizerJsonPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var vocab = doc.RootElement.GetProperty("model").GetProperty("vocab");

        // vocab is a JSON object: { "token": id, ... }
        // Sort tokens by ID so line index == token ID.
        var pairs = new List<(int Id, string Token)>();
        foreach (var prop in vocab.EnumerateObject())
            pairs.Add((prop.Value.GetInt32(), prop.Name));
        pairs.Sort((a, b) => a.Id.CompareTo(b.Id));

        using var writer = new StreamWriter(vocabPath);
        foreach (var (_, token) in pairs)
            writer.WriteLine(token);
    }
}
