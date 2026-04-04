---
name: ingest
description: Use when user says "/memory ingest", "add to knowledge base", "ingest these docs", or asks to import files or directories into total-recall.
---

# Knowledge Base Ingestion

Add files or directories to the knowledge base for semantic retrieval.

## Process

1. Determine if the path is a file or directory
2. For files: call `kb_ingest_file` with the path
3. For directories: call `kb_ingest_dir` with the path
4. Report: collection name, document count, chunk count, validation results
5. Suggest a test query to verify ingestion worked

## Supported Formats

Markdown, TypeScript, JavaScript, Python, Go, Rust, JSON, YAML, plain text. Code files are split on function/class boundaries. Markdown is split on headings.
