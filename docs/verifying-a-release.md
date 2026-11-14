# Verifying a release

Each GitHub Release carries a build attestation (GitHub artifact attestations, signed through sigstore) for its four platform archives and for `provisioning.manifest.json`, produced by this repository's `release.yml`. To check a file you downloaded from a release:

```bash
gh auth login    # once: `gh attestation verify` needs an authenticated GitHub CLI
gh attestation verify total-recall-linux-x64.tar.gz --repo strvmarv/total-recall
```

Use the archive for your platform (`total-recall-<rid>.tar.gz`) or `provisioning.manifest.json`. To also require that the attestation came from this repository's release workflow, add `--signer-workflow strvmarv/total-recall/.github/workflows/release.yml`.

Only releases published after attestations were added carry them. The plugin's own launcher does not check attestations: before it extracts a download it verifies only the sha256 listed in `provisioning.manifest.json`, so the attestation is for people who want to verify what they run, not something the install enforces.
