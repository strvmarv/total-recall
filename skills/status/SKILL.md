---
name: status
description: Use when user says "/memory status", "/memory eval", "how is memory performing", "show memory dashboard", or asks about total-recall health or metrics.
---

# Memory Status & Evaluation

Show the TUI dashboard or detailed evaluation metrics.

## For /memory status

Call the `status` MCP tool and format the response as a dashboard showing:
- Tier sizes (hot/warm/cold with counts)
- Knowledge base stats (collections, documents, chunks)
- DB size and embedding model info
- Session activity summary

## For /memory eval

Call `eval_report` for live metrics. Show:
- Precision@3, hit rate, miss rate, MRR (7-day rolling)
- Breakdown by tier and content type
- Top misses and false positives
- Compaction health

## For /memory eval --benchmark

Call `eval_benchmark`. Show synthetic benchmark results with pass/fail thresholds.

## For /memory eval --compare <name>

Call `eval_report` with the named config snapshot for comparison. Show side-by-side metrics with deltas and trend arrows.
