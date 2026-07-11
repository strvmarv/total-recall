#!/usr/bin/env node
// Shared binary-fetching logic used by both scripts/postinstall.js and
// bin/start.js. Downloads the correct .NET AOT publish archive for the
// host platform from the matching GitHub Release asset, extracts it into
// ${repoRoot}/binaries/<rid>/, and returns the path to the executable.
//
// Why an archive and not a bare executable: the AOT binary has sibling
// native runtime dependencies (libonnxruntime.dylib, vec0.dylib, the
// bundled models/ subtree, etc.) that must land next to it on disk.
// 0.8.0-beta.4 shipped the bare executable as a GitHub Release asset
// and every fresh install crashed at first DB open with a
// TypeInitializationException because Microsoft.ML.OnnxRuntime could
// not P/Invoke into the missing libonnxruntime.dylib. Fix: release.yml
// now stages each per-RID publish tree into a .tar.gz (.zip on Windows)
// and this module fetches + extracts the archive so the full tree
// lands on disk before start.js exec's it.
//
// Why this exists: the npm tarball ships binaries/<rid>/... with the
// full publish tree already included, but when Claude Code installs
// the plugin via git clone (e.g. /plugin update from a marketplace
// that points at the git repo), the tree has no binaries/ because
// they are never committed. This module bridges that gap: postinstall
// downloads at install time for npm users, and bin/start.js downloads
// at first launch as a safety net for git-clone users and
// --ignore-scripts users.
//
// Zero dependencies — Node built-ins plus the `tar`/`unzip` binaries
// that ship on every supported target (macOS + Linux have tar, Windows
// 10+ ships bsdtar as `tar.exe` and PowerShell has Expand-Archive).
//
// Contract:
//   import { ensureBinary } from './fetch-binary.js';
//   const result = await ensureBinary();
//   // result.ok === true  -> result.path points at an executable binary
//   // result.ok === false -> result.error is a human-readable message

import crypto from 'node:crypto';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync, spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { pipeline } from 'node:stream/promises';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');

// RID detection must stay in lockstep with bin/start.js and with the
// matrix in .github/workflows/release.yml. Intel Mac is intentionally
// not shipped.
export function detectRid(platform, arch) {
  if (platform === 'linux' && arch === 'x64') return 'linux-x64';
  if (platform === 'linux' && arch === 'arm64') return 'linux-arm64';
  if (platform === 'darwin' && arch === 'arm64') return 'osx-arm64';
  if (platform === 'win32' && arch === 'x64') return 'win-x64';
  return null;
}

export function getBinaryName(rid) {
  return rid === 'win-x64' ? 'total-recall.exe' : 'total-recall';
}

export function getBinaryPath(rid) {
  return path.join(repoRoot, 'binaries', rid, getBinaryName(rid));
}

function getVersion() {
  const pkgPath = path.join(repoRoot, 'package.json');
  const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
  if (!pkg.version) {
    throw new Error(`package.json at ${pkgPath} has no version field`);
  }
  return pkg.version;
}

// Release asset filenames are set by release.yml's "Stage per-RID release
// assets" step. All RIDs (including win-x64) ship as .tar.gz — Windows 10+
// has native tar.exe / bsdtar that handles .tar.gz fine, and using a single
// archive format avoids the GNU-tar-vs-bsdtar gotcha that broke
// v0.8.0-beta.6's win-x64.zip (it was actually a POSIX tar archive because
// the publish runner is ubuntu-latest where `tar` is GNU tar, and GNU tar's
// -a treats .zip as "no compression", producing a misnamed file). URL
// format:
//   https://github.com/strvmarv/total-recall/releases/download/v<version>/total-recall-<rid>.tar.gz
export function getArchiveName(rid) {
  return `total-recall-${rid}.tar.gz`;
}

export function getDownloadUrl(rid, version) {
  return `https://github.com/strvmarv/total-recall/releases/download/v${version}/${getArchiveName(rid)}`;
}

export function getManifestUrl(version) {
  return `https://github.com/strvmarv/total-recall/releases/download/v${version}/provisioning.manifest.json`;
}

// Streaming sha256 so we never load a ~90 MB archive fully into memory.
// pipeline() tears the read stream down (closes the fd) even on error, which
// the hand-rolled 'end'-listener version did not guarantee.
export async function sha256File(filePath) {
  const hash = crypto.createHash('sha256');
  await pipeline(fs.createReadStream(filePath), async function* (source) {
    for await (const chunk of source) hash.update(chunk);
  });
  return hash.digest('hex');
}

// Security-critical comparison, extracted from ensureBinary so it can be tested
// hermetically (no network). Returns null when the file matches expectedSha256;
// otherwise an error-result object. checksumMismatch is set ONLY on an actual
// digest mismatch (corrupt/tampered asset, not retryable) — a read failure is a
// plain error so callers may retry it.
export async function verifyArchiveChecksum(filePath, expectedSha256, archiveName = 'archive') {
  let actual;
  try {
    actual = await sha256File(filePath);
  } catch (e) {
    return { ok: false, error: `checksum read failed: ${e.message}` };
  }
  if (actual.toLowerCase() !== expectedSha256.toLowerCase()) {
    return {
      ok: false,
      error: `checksum mismatch for ${archiveName}: expected ${expectedSha256}, got ${actual}`,
      checksumMismatch: true,
    };
  }
  return null;
}

// Cheap, size-only integrity check of the extracted model payload against the
// registry that ships inside the same tree (models/registry.json). The
// archive-sha256 gate proves the *tarball* was byte-perfect, but it stops at the
// tarball boundary — an extraction interrupted partway (process killed mid-tar)
// leaves the engine binary present yet the ~133 MB model.onnx truncated, and
// nothing downstream ever notices. This function is that missing check: it
// compares each model.onnx's on-disk size to the registry's sizeBytes.
//
// A stat() is used deliberately (not a re-hash) so the check is fast enough to
// run on every launch's fast path — a truncated 1.5 MB model vs the expected
// 133 MB is caught instantly. Byte-correctness of a full-sized file is already
// guaranteed upstream by the archive checksum, so size is the right, cheap
// signal for extraction completeness.
//
// Returns { ok: true } when intact (or when there is no registry to check
// against — absence of a registry is not evidence of corruption), otherwise
// { ok: false, reason }.
export function payloadIntact(destDir) {
  const registryPath = path.join(destDir, 'models', 'registry.json');
  // A genuinely-absent registry (ENOENT) means there is nothing to validate —
  // legacy / non-model trees. But a registry that is PRESENT yet unreadable or
  // unparseable is itself a signature of an interrupted extraction (the file was
  // cut mid-write), so it must NOT be waved through as "intact" — that would
  // defeat the self-heal guard. Distinguish the two.
  let raw;
  try {
    raw = fs.readFileSync(registryPath, 'utf8');
  } catch (e) {
    if (e && e.code === 'ENOENT') return { ok: true }; // no registry -> nothing to validate
    return { ok: false, reason: `registry.json unreadable: ${e.message}` };
  }
  let registry;
  try {
    registry = JSON.parse(raw);
  } catch (e) {
    return { ok: false, reason: `registry.json is corrupt or truncated: ${e.message}` };
  }
  const models = registry && registry.models;
  if (!models || typeof models !== 'object') return { ok: true };

  for (const [name, spec] of Object.entries(models)) {
    // sizeBytes in the registry is the size of the model.onnx payload file.
    const expected = spec && spec.sizeBytes;
    if (typeof expected !== 'number') continue;
    const modelPath = path.join(destDir, 'models', name, 'model.onnx');
    let actual = -1;
    try { actual = fs.statSync(modelPath).size; } catch {}
    if (actual !== expected) {
      return { ok: false, reason: `model ${name} size ${actual} != expected ${expected}` };
    }
  }
  return { ok: true };
}

// Marker written next to the installed binary after a verified download, so a
// later launch trusts presence + version match and skips re-hashing 90 MB.
function verifiedMarkerPath(rid) { return path.join(repoRoot, 'binaries', rid, '.verified.json'); }

export function readVerifiedMarker(rid) {
  try {
    const { version, sha256 } = JSON.parse(fs.readFileSync(verifiedMarkerPath(rid), 'utf8'));
    if (typeof version !== 'string' || typeof sha256 !== 'string') return null;
    return { version, sha256 };
  } catch { return null; }
}

export function writeVerifiedMarker(rid, version, sha256) {
  try { fs.writeFileSync(verifiedMarkerPath(rid), JSON.stringify({ version, sha256 })); } catch { /* best-effort */ }
}

function httpGetFollowRedirects(url, redirectsLeft = 5) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, (res) => {
      const status = res.statusCode ?? 0;
      if (status >= 300 && status < 400) {
        if (redirectsLeft <= 0) {
          res.resume();
          reject(new Error(`Too many redirects following ${url}`));
          return;
        }
        const loc = res.headers.location;
        if (!loc) {
          res.resume();
          reject(new Error(`Redirect ${status} with no Location header from ${url}`));
          return;
        }
        res.resume();
        const nextUrl = loc.startsWith('http') ? loc : new URL(loc, url).toString();
        httpGetFollowRedirects(nextUrl, redirectsLeft - 1).then(resolve).catch(reject);
        return;
      }
      if (status !== 200) {
        res.resume();
        reject(new Error(`HTTP ${status} for ${url}`));
        return;
      }
      resolve(res);
    });
    req.on('error', reject);
  });
}

// Stream the response body to destPath. The optional onData(chunkLength)
// callback is invoked for every received chunk so callers can count bytes;
// it must not throw (counting/progress reporting is best-effort and must
// never abort the download). Error handling stays symmetric: a failure on
// either the read or write side destroys the other and rejects.
async function streamToFile(res, destPath, onData) {
  await new Promise((resolve, reject) => {
    const out = fs.createWriteStream(destPath);
    if (typeof onData === 'function') {
      res.on('data', (chunk) => { try { onData(chunk.length); } catch {} });
    }
    res.pipe(out);
    out.on('finish', resolve);
    out.on('error', (e) => {
      try { res.destroy(); } catch {}
      reject(e);
    });
    res.on('error', (e) => {
      try { out.destroy(); } catch {}
      reject(e);
    });
  });
}

// Resolve the tar executable. On Windows a GNU tar earlier on PATH (e.g.
// from Git for Windows / msys) treats an absolute destination like
// `C:\...` as a remote host spec ("Cannot connect to C:"), so we pin to
// the bundled bsdtar at %SystemRoot%\System32\tar.exe when present.
function tarExecutable() {
  if (process.platform === 'win32') {
    const sysTar = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe');
    if (fs.existsSync(sysTar)) return sysTar;
  }
  return 'tar';
}

// Extract a .tar.gz archive into destDir. destDir must already exist.
// Uses system `tar` (GNU tar on Linux, BSD tar on macOS, bsdtar/libarchive
// shipped as `tar.exe` on Windows 10+ since build 17063 / 1803). All
// platforms accept the same `tar -xzf <archive> -C <dest>` invocation,
// so there is no per-platform branch and no PowerShell fallback. Throws
// on failure — callers wrap in try/catch and clean up the archive.
function extractArchive(archivePath, destDir) {
  execFileSync(tarExecutable(), ['-xzf', archivePath, '-C', destDir], { stdio: 'inherit' });
}

export async function ensureBinary({ logPrefix = '[total-recall]', onProgress, expectedSha256 = null, url: urlOverride = null } = {}) {
  const report = typeof onProgress === 'function' ? onProgress : () => {};
  const { platform, arch } = process;
  const rid = detectRid(platform, arch);

  if (!rid) {
    const isIntelMac = platform === 'darwin' && arch === 'x64';
    const note = isIntelMac
      ? ' (Intel Macs are not shipped; all modern Apple hardware is arm64)'
      : '';
    return {
      ok: false,
      error: `unsupported platform: ${platform}/${arch}${note}`,
    };
  }

  const binaryPath = getBinaryPath(rid);
  const destDir = path.dirname(binaryPath);
  // Present-binary fast path — but only trust it when the model payload is also
  // intact. A binary present with a truncated/missing model (interrupted prior
  // extraction) must NOT be trusted forever; fall through to a fresh download so
  // provisioning self-heals instead of leaving a permanently-broken tree.
  if (fs.existsSync(binaryPath) && payloadIntact(destDir).ok) {
    let sizeBytes = 0;
    try { sizeBytes = fs.statSync(binaryPath).size; } catch {}
    return { ok: true, path: binaryPath, rid, downloaded: false, sizeBytes };
  }

  let version;
  try {
    version = getVersion();
  } catch (e) {
    return { ok: false, error: `could not read package.json version: ${e.message}` };
  }

  const url = urlOverride ?? getDownloadUrl(rid, version);

  // Stage archive download into a fresh tmp file alongside destDir so an
  // abort (Ctrl+C, crash, network failure) cannot leave a partially
  // extracted binaries/<rid>/ behind. We still clean up destDir-level
  // staging files on error.
  const archiveName = getArchiveName(rid);
  const tmpArchivePath = path.join(os.tmpdir(), `total-recall-${process.pid}-${Date.now()}-${archiveName}`);

  process.stderr.write(`${logPrefix} Downloading ${rid} archive for v${version}\n`);
  process.stderr.write(`${logPrefix}   ${url}\n`);

  try {
    fs.mkdirSync(destDir, { recursive: true });
  } catch (e) {
    return { ok: false, error: `could not create ${destDir}: ${e.message}`, url };
  }

  let received = 0;
  try {
    const res = await httpGetFollowRedirects(url);
    const total = Number(res.headers['content-length']) || 0;
    // Throttle progress reports so we don't rewrite the lock file (a disk
    // write) on every TCP chunk: only emit when ≥500 ms has elapsed since
    // the last report OR ≥2 MB more has arrived.
    let lastReportAt = 0;
    let lastReportBytes = 0;
    await streamToFile(res, tmpArchivePath, (len) => {
      received += len;
      const now = Date.now();
      if (now - lastReportAt >= 500 || received - lastReportBytes >= 2 * 1024 * 1024) {
        lastReportAt = now;
        lastReportBytes = received;
        report({ bytes: received, total, phase: 'downloading' });
      }
    });
    // Final downloading tick so consumers see 100% before extraction starts.
    report({ bytes: received, total, phase: 'downloading' });
    report({ bytes: received, total, phase: 'extracting' });
  } catch (e) {
    try { fs.rmSync(tmpArchivePath, { force: true }); } catch {}
    return {
      ok: false,
      error: `download failed: ${e.message}`,
      url,
    };
  }

  // NEVER extract an unverified archive when a checksum was supplied. Opt-in:
  // callers that pass no expectedSha256 (postinstall, runProvision) skip this.
  if (expectedSha256 != null) {
    const bad = await verifyArchiveChecksum(tmpArchivePath, expectedSha256, archiveName);
    if (bad) {
      try { fs.rmSync(tmpArchivePath, { force: true }); } catch {}
      return { ...bad, url };
    }
  }

  try {
    extractArchive(tmpArchivePath, destDir);
  } catch (e) {
    try { fs.rmSync(tmpArchivePath, { force: true }); } catch {}
    return {
      ok: false,
      error: `extract failed: ${e.message}`,
      url,
    };
  } finally {
    try { fs.rmSync(tmpArchivePath, { force: true }); } catch {}
  }

  if (!fs.existsSync(binaryPath)) {
    return {
      ok: false,
      error: `archive extracted but ${binaryPath} is missing — archive layout does not match expected binaries/<rid>/ contents`,
      url,
    };
  }

  // The archive was byte-perfect (checksum passed above), but tar can still be
  // interrupted mid-extraction — leaving the binary present yet the ~133 MB
  // model.onnx truncated at a block boundary. Validate the extracted payload
  // before stamping success, so we never write a .verified marker over a broken
  // tree. Returned as retryable (no checksumMismatch) so the caller re-provisions.
  const intact = payloadIntact(destDir);
  if (!intact.ok) {
    return {
      ok: false,
      error: `archive extracted but model payload is incomplete (${intact.reason}) — extraction may have been interrupted`,
      url,
    };
  }

  // Restore executable bit. tar preserves modes but some CI runners
  // strip them on upload/download round-trips, and unzip on Unix
  // normalizes to 0644. Force 0755 on the main binary; siblings are
  // libraries that don't need it.
  if (platform !== 'win32') {
    try {
      fs.chmodSync(binaryPath, 0o755);
    } catch (e) {
      return {
        ok: false,
        error: `could not chmod +x ${binaryPath}: ${e.message}`,
        url,
      };
    }
  }

  if (expectedSha256 != null) writeVerifiedMarker(rid, version, expectedSha256);

  process.stderr.write(`${logPrefix} Installed at ${binaryPath}\n`);
  return { ok: true, path: binaryPath, rid, downloaded: true, sizeBytes: received };
}

// Background provisioning ----------------------------------------------------
//
// On the git/marketplace install path the binary is missing at first launch
// and the ~90 MB download cannot happen on the MCP startup handshake (Claude
// Code's stdio startup timeout is a hard wall-clock with no retry). Instead we
// spawn a DETACHED background downloader and let start.js exit fast; the next
// launch finds the binary and starts instantly.

function provisionLockPath(rid) { return path.join(repoRoot, 'binaries', `.provision-${rid}.lock`); }

// Post-provision marker the .NET server reads once to surface "first-run
// setup complete" to the user. Lives next to the installed binary. Shape is a
// shared contract with the server: { version, sizeBytes, durationMs, completedAtUnixMs }.
function provisionedMarkerPath(rid) { return path.join(repoRoot, 'binaries', rid, '.provisioned.json'); }

// Read in-flight download progress for start.js to display on a subsequent
// launch while a detached provisioner is still running. The provisioner
// rewrites the lock file with {pid, startedAt, bytes, total, phase} as bytes
// arrive; returns {bytes, total, phase} or null if the lock is absent,
// unparseable, or has not yet recorded progress fields.
export function readProvisionProgress(rid) {
  try {
    const { bytes, total, phase } = JSON.parse(fs.readFileSync(provisionLockPath(rid), 'utf8'));
    if (typeof bytes !== 'number' || typeof total !== 'number' || typeof phase !== 'string') return null;
    return { bytes, total, phase };
  } catch { return null; }
}

function provisionerAlive(lockPath) {
  try {
    const { pid, startedAt } = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
    if (typeof pid !== 'number') return false;
    // Hard cap also fires when startedAt is missing/corrupt (e.g. a lock from an
    // older build), so a reused-PID collision can't keep a dead lock alive forever.
    if (typeof startedAt !== 'number' || Date.now() - startedAt > 30 * 60 * 1000) return false;
    process.kill(pid, 0); // throws if not alive
    return true;
  } catch { return false; }
}

// status: 'present' | 'started' | 'in-progress' | 'unsupported'
export function provisionInBackground({ logPrefix = '[total-recall]' } = {}) {
  const rid = detectRid(process.platform, process.arch);
  if (!rid) return { status: 'unsupported', rid: null };
  if (fs.existsSync(getBinaryPath(rid))) return { status: 'present', rid };

  const lockPath = provisionLockPath(rid);
  try { fs.mkdirSync(path.dirname(lockPath), { recursive: true }); } catch {}
  if (fs.existsSync(lockPath) && provisionerAlive(lockPath)) return { status: 'in-progress', rid };

  const child = spawn(process.execPath, [fileURLToPath(import.meta.url), '--provision'],
    { detached: true, stdio: 'ignore', windowsHide: true });
  child.unref();
  try { fs.writeFileSync(lockPath, JSON.stringify({ pid: child.pid, startedAt: Date.now() })); } catch {}
  return { status: 'started', rid };
}

async function runProvision() {
  const rid = detectRid(process.platform, process.arch);
  const lockPath = rid ? provisionLockPath(rid) : null;

  // The parent wrote {pid, startedAt} into the lock; preserve startedAt so
  // provisionerAlive's liveness/age checks keep working across our rewrites.
  let startedAt = Date.now();
  if (lockPath) {
    try {
      const prev = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
      if (typeof prev.startedAt === 'number') startedAt = prev.startedAt;
    } catch { /* fall back to now */ }
  }
  const start = Date.now();

  // Rewrite the lock as {pid, startedAt, bytes, total, phase} on each progress
  // tick. pid + startedAt are preserved verbatim so provisionerAlive (which
  // only reads those two) keeps reporting this provisioner as alive. Best-effort.
  const onProgress = lockPath
    ? ({ bytes, total, phase }) => {
        try {
          fs.writeFileSync(lockPath, JSON.stringify({ pid: process.pid, startedAt, bytes, total, phase }));
        } catch { /* progress is a nicety, not load-bearing */ }
      }
    : undefined;

  try {
    const result = await ensureBinary({ logPrefix: '[total-recall:provision]', onProgress });
    // Drop the post-provision marker BEFORE the finally removes the lock, so
    // the server has a stable signal even if the lock is gone. Best-effort.
    if (result && result.ok === true && rid) {
      try {
        fs.writeFileSync(provisionedMarkerPath(rid), JSON.stringify({
          version: getVersion(),
          sizeBytes: result.sizeBytes ?? 0,
          durationMs: Date.now() - start,
          completedAtUnixMs: Date.now(),
        }));
      } catch { /* marker is a nicety, not load-bearing */ }
    }
  }
  catch { /* best-effort */ }
  finally { if (lockPath) { try { fs.rmSync(lockPath, { force: true }); } catch {} } }
}

// Run as a detached provisioner when invoked directly with --provision.
// Resolve both sides through realpath so a symlinked invocation still matches
// (path.resolve alone would not follow the link). Falls back to a plain resolve
// if realpath fails. The spawn above always passes the real module path, so this
// only matters for manual invocation.
function isDirectProvisionInvocation() {
  if (!process.argv[1] || !process.argv.includes('--provision')) return false;
  const real = (p) => { try { return fs.realpathSync(p); } catch { return path.resolve(p); } };
  return real(process.argv[1]) === real(fileURLToPath(import.meta.url));
}
if (isDirectProvisionInvocation()) {
  runProvision();
}
