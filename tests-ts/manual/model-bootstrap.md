# Manual smoke test: model bundle resilience

These checks exercise the bootstrap state machine end-to-end against the real filesystem and (in some cases) the real HuggingFace download. Run them after any change to `src/embedding/bootstrap.ts`, `src/embedding/model-manager.ts`, or `src/embedding/embedder.ts`.

## Setup

- [ ] Build the project: `npm run build`
- [ ] Identify the user data dir for total-recall (usually `~/.total-recall/models/all-MiniLM-L6-v2/`)

## Scenario 1: Healthy bundled model (happy path)

- [ ] Confirm `models/all-MiniLM-L6-v2/model.onnx` exists in the source tree and is the real ~90 MB binary (not an LFS pointer): `ls -lh models/all-MiniLM-L6-v2/model.onnx` should show ~90M
- [ ] Remove any cached `.verified` sidecar in the bundled dir to force a checksum on next start
- [ ] Start a fresh MCP session (e.g., open a new Claude Code window with the plugin loaded)
- [ ] First call to `session_start` should succeed within a few seconds (checksum is computed once)
- [ ] Subsequent calls should return immediately (`.verified` sidecar shortcuts checksum)
- [ ] Verify `.verified` file now exists in the bundled model dir and contains exactly the expected sha256

## Scenario 2: Bundled LFS pointer (the original bug)

- [ ] Back up the real `models/all-MiniLM-L6-v2/model.onnx`
- [ ] Replace it with a 133-byte LFS pointer file (copy from `tests/fixtures/lfs-pointer-model.onnx`)
- [ ] Remove the user data dir model: `rm -rf ~/.total-recall/models/all-MiniLM-L6-v2`
- [ ] Start a fresh MCP session
- [ ] First call to `session_start` should return a structured `model_not_ready` error with `reason: "downloading"`
- [ ] Wait 30–60 seconds for the download to complete
- [ ] Call `session_start` again — should succeed and report normal startup
- [ ] Verify the model now exists in `~/.total-recall/models/all-MiniLM-L6-v2/` with `model.onnx` ~90 MB and a `.verified` sidecar
- [ ] Restore the real bundled model from backup

## Scenario 3: Concurrent processes (lockfile)

- [ ] Remove the user data dir model: `rm -rf ~/.total-recall/models/all-MiniLM-L6-v2`
- [ ] Replace the bundled model with the LFS pointer (as in Scenario 2)
- [ ] Open TWO Claude Code windows simultaneously, both with total-recall loaded
- [ ] In both windows, ask: "Run /total-recall status"
- [ ] Only ONE process should actually download (check `~/.total-recall/models/all-MiniLM-L6-v2/.bootstrap.lock` exists during download)
- [ ] Both processes should eventually succeed and reach a healthy state
- [ ] Restore the real bundled model

## Scenario 4: Network failure

- [ ] Remove the user data dir model
- [ ] Replace bundled with LFS pointer
- [ ] Disable network (airplane mode or block huggingface.co in /etc/hosts)
- [ ] Start a fresh MCP session
- [ ] `session_start` should return a structured `model_not_ready` error with `reason: "failed"` and a `hint` containing the manual install commands
- [ ] The assistant (using the using-total-recall skill) should surface the hint and continue without memory features
- [ ] Re-enable network, call `session_start` again — should succeed
- [ ] Restore the real bundled model

## Scenario 5: Corrupt download (sha256 mismatch)

- [ ] Remove the user data dir model
- [ ] Replace bundled with LFS pointer
- [ ] In `models/registry.json`, temporarily change the sha256 for `all-MiniLM-L6-v2` to all zeros
- [ ] Start a fresh MCP session — bootstrap should download then fail with `reason: "corrupted"`
- [ ] Verify the partially-downloaded `model.onnx` was removed (best-effort cleanup)
- [ ] Verify NO `.verified` sidecar was written
- [ ] Revert the registry.json change and restore the real bundled model

## Cleanup

- [ ] Restore real bundled model
- [ ] Run `npm test` and confirm vitest unit tests still pass
- [ ] Remove any leftover `.tmp.*` files in the model dirs
