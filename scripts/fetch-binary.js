#!/usr/bin/env node
// Shared binary-fetching logic used by both scripts/postinstall.js and
// bin/start.js. Downloads the correct .NET AOT binary for the host
// platform from the matching GitHub Release asset and installs it under
// ${repoRoot}/binaries/<rid>/.
//
// Why this exists: the npm tarball ships binaries/<rid>/... for every
// supported platform, but when Claude Code installs the plugin via git
// clone (e.g. /plugin update from a marketplace that points at the git
// repo), the tree has no binaries/ because they are never committed to
// git. This module bridges that gap: postinstall downloads at install
// time for npm users, and bin/start.js downloads at first launch as a
// safety net for git-clone users and --ignore-scripts users.
//
// Zero dependencies — Node built-ins only.
//
// Contract:
//   import { ensureBinary } from './fetch-binary.js';
//   const result = await ensureBinary();
//   // result.ok === true  -> result.path points at an executable binary
//   // result.ok === false -> result.error is a human-readable message

import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
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
// assets" step. URL format:
//   https://github.com/strvmarv/total-recall/releases/download/v<version>/total-recall-<rid>[.exe]
export function getDownloadUrl(rid, version) {
  const ext = rid === 'win-x64' ? '.exe' : '';
  return `https://github.com/strvmarv/total-recall/releases/download/v${version}/total-recall-${rid}${ext}`;
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
  const tmpPath = binaryPath + '.tmp';

  process.stderr.write(`${logPrefix} Downloading ${rid} binary for v${version}\n`);
  process.stderr.write(`${logPrefix}   ${url}\n`);

  try {
    fs.mkdirSync(destDir, { recursive: true });
  } catch (e) {
    return { ok: false, error: `could not create ${destDir}: ${e.message}`, url };
  }

  // Clean up any stale tmp from a previous aborted attempt.
  try { fs.rmSync(tmpPath, { force: true }); } catch {}

  try {
    const res = await httpGetFollowRedirects(url);
    await streamToFile(res, tmpPath);
    fs.renameSync(tmpPath, binaryPath);
    if (platform !== 'win32') {
      fs.chmodSync(binaryPath, 0o755);
    }
    process.stderr.write(`${logPrefix} Installed at ${binaryPath}\n`);
    return { ok: true, path: binaryPath, rid, downloaded: true };
  } catch (e) {
    try { fs.rmSync(tmpPath, { force: true }); } catch {}
    return {
      ok: false,
      error: `download failed: ${e.message}`,
      url,
    };
  }
}
