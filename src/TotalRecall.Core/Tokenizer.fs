module TotalRecall.Core.Tokenizer

// Canonical BERT BasicTokenization + WordPiece.
// Pure function over input text → list of token IDs.
// No I/O, no state beyond the vocab passed in.

/// A vocabulary map: token string → token ID.
type Vocab = Map<string, int>

/// Tokenize text using BERT BasicTokenization + WordPiece against the given vocab.
/// Returns a list of token IDs including CLS (101) at start and SEP (102) at end.
/// OOV tokens map to UNK (100). Truncates to MAX_SEQ_LEN (512).
let tokenize (vocab: Vocab) (text: string) : int list =
    failwith "TotalRecall.Core.Tokenizer.tokenize not yet implemented (Plan 2 Task 2.4)"
