# Configuration

The config file lives at `~/.total-recall/config.toml`. All fields have defaults — you only need to override what you want to change.

```toml
# total-recall configuration

[tiers.pinned]
# The Pinned tier was merged into Hot as a "sticky" flag in 4.0. This section
# is kept as a deprecated alias — its floor_* / project_scoping fields still
# drive the per-turn re-injection of sticky pins. max_content_chars no longer
# applies (sticky entries are unbounded).
floor_enabled = true              # Per-turn pinned-directive floor (UserPromptSubmit re-injection)
floor_every_n_turns = 6           # Re-inject the sticky block at least every N user turns
floor_growth_tokens = 6000        # ...or after ~this many tokens of transcript growth (whichever trips first)
project_scoping = true            # Scope sticky injection by git repo: repo-tagged pins inject only in their repo; untagged pins are global; fail-closed when no repo detected

[tiers.hot]
max_entries = 50                  # Max entries injected at session start
token_budget = 4000               # Max tokens for hot tier injection (sticky pins come off the top)
carry_forward_threshold = 0.7     # Score threshold to stay in hot
max_content_chars = 1200          # Max characters per hot entry (oversize rejected — store a summary or keep it in warm); sticky pins are exempt

[tiers.warm]
max_entries = 10000               # Max entries in warm tier
retrieval_top_k = 5               # Results returned per search
similarity_threshold = 0.65       # Min cosine similarity for retrieval
cold_decay_days = 30              # Days before unused warm entries decay to cold

[tiers.cold]
chunk_max_tokens = 512            # Max tokens per knowledge base chunk
chunk_overlap_tokens = 50         # Overlap between adjacent chunks
lazy_summary_threshold = 5        # Accesses before generating summary

[compaction]
decay_half_life_hours = 168       # Score half-life (168h = 1 week)
warm_threshold = 0.3              # Score below which warm→cold
promote_threshold = 0.7           # Min decay_score to promote (cold→warm, and warm→hot)
promote_min_access = 5            # Min access_count for warm→hot promotion (paired with promote_threshold on decay_score)
warm_sweep_interval_days = 7      # How often to run warm sweep

[search]
fts_weight = 0.3                  # BM25 weight in hybrid ranking (0.0 = vector only, 1.0 = FTS only)

[scope]
default = "user:local"            # Default scope for new entries (e.g., "user", "team")

[regression]
miss_rate_delta = 0.1             # Alert if miss rate increased by this much vs. previous snapshot
latency_ratio = 2.0               # Alert if latency increased by this factor vs. previous snapshot
min_events = 10                   # Minimum retrieval events required on each side before the regression check runs

[embedding]
model = "bge-small-en-v1.5"      # Embedding model name
dimensions = 384                  # Embedding dimensions
# provider = "local"              # "local" (default) | "openai" | "bedrock"
# endpoint = "https://api.openai.com/v1"   # OpenAI-compatible base URL
# bedrock_region = "us-east-1"             # Bedrock only
# bedrock_model = "cohere.embed-v4:0"      # Bedrock model ID
# api_key = ""                             # or set TOTAL_RECALL_EMBEDDING_API_KEY env var

# --- Skills (optional) ---
# [skills]
# extra_dirs = [
#   "~/my-skills",
#   "/path/to/team-skills"
# ]

# --- Remote storage (optional) ---
# [storage]
# connection_string = "Host=localhost;Database=total_recall;Username=tr;Password=changeme"

# --- User identity (optional, Postgres only) ---
# [user]
# user_id = "alice"               # or set TOTAL_RECALL_USER_ID env var
```

**Relocating the database:** set `TOTAL_RECALL_DB_PATH` to an absolute path or `~/`-prefixed path. See [Relocating the database](database-location.md) for cloud-sync and shared-workspace guidance.

**Switching to Postgres:** uncomment the `[storage]` section with your connection string. The binary auto-detects the backend — no code changes, no flag. Pair with `[embedding] provider = "bedrock"` or `"openai"` for remote embeddings. Run `migrate_to_remote` to copy local memories to the shared database with re-embedding.
