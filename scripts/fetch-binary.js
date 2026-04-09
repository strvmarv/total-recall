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

import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

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

async function streamToFile(res, destPath) {
  await new Promise((resolve, reject) => {
    const out = fs.createWriteStream(destPath);
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

// Extract a .tar.gz archive into destDir. destDir must already exist.
// Uses system `tar` (GNU tar on Linux, BSD tar on macOS, bsdtar/libarchive
// shipped as `tar.exe` on Windows 10+ since build 17063 / 1803). All
// platforms accept the same `tar -xzf <archive> -C <dest>` invocation,
// so there is no per-platform branch and no PowerShell fallback. Throws
// on failure — callers wrap in try/catch and clean up the archive.
function extractArchive(archivePath, destDir) {
  execFileSync('tar', ['-xzf', archivePath, '-C', destDir], { stdio: 'inherit' });
}

export async function ensureBinary({ logPrefix = '[total-recall]' } = {}) {
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
  if (fs.existsSync(binaryPath)) {
    return { ok: true, path: binaryPath, rid, downloaded: false };
  }

  let version;
  try {
    version = getVersion();
  } catch (e) {
    return { ok: false, error: `could not read package.json version: ${e.message}` };
  }

  const url = getDownloadUrl(rid, version);
  const destDir = path.dirname(binaryPath);

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

  try {
    const res = await httpGetFollowRedirects(url);
    await streamToFile(res, tmpArchivePath);
  } catch (e) {
    try { fs.rmSync(tmpArchivePath, { force: true }); } catch {}
    return {
      ok: false,
      error: `download failed: ${e.message}`,
      url,
    };
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

  process.stderr.write(`${logPrefix} Installed at ${binaryPath}\n`);
  return { ok: true, path: binaryPath, rid, downloaded: true };
}
