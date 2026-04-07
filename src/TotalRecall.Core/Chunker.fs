module TotalRecall.Core.Chunker

// Text chunking with overlap and token budget enforcement.

type ChunkOptions = {
    MaxTokens: int
    OverlapTokens: int
}

type Chunk = {
    Content: string
    TokenEstimate: int
}

/// Split text into chunks respecting max tokens with token-overlap between chunks.
let chunkText (options: ChunkOptions) (text: string) : Chunk list =
    failwith "TotalRecall.Core.Chunker.chunkText not yet implemented (Plan 2 Task 2.11)"
