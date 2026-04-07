#!/usr/bin/env node
// Downloads a pinned Bun binary to ~/.total-recall/bun/<version>/bun
// Runs during `npm install`. Failures are non-fatal (exit 0 with warning).

import https from "node:https";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";

const BUN_VERSION = "1.2.10";

const PLATFORM_MAP = {
  "linux-x64": "bun-linux-x64",
  "linux-arm64": "bun-linux-aarch64",
  "darwin-x64": "bun-darwin-x64",
  "darwin-arm64": "bun-darwin-aarch64",
  "win32-x64": "bun-windows-x64",
};

function getBunDir() {
  return path.join(os.homedir(), ".total-recall", "bun", BUN_VERSION);
}

function getBunBin() {
  const ext = process.platform === "win32" ? ".exe" : "";
  return path.join(getBunDir(), `bun${ext}`);
}

function getPlatformKey() {
  const arch = process.arch === "x64" ? "x64" : process.arch === "arm64" ? "arm64" : null;
  if (!arch) return null;
  return `${process.platform}-${arch}`;
}

function warn(msg) {
  process.stderr.write(`total-recall postinstall: ${msg}\n`);
}

function download(url, destPath) {
  return new Promise((resolve, reject) => {
    const follow = (u, redirectsLeft = 5) => {
      https.get(u, (res) => {
        if (res.statusCode === 301 || res.statusCode === 302) {
          const loc = res.headers.location;
          if (!loc) {
            reject(new Error(`Redirect with no Location header from ${u}`));
            return;
          }
          if (redirectsLeft <= 0) {
            reject(new Error(`Too many redirects following ${url}`));
            return;
          }
          follow(loc, redirectsLeft - 1);
          return;
        }
        if (res.statusCode !== 200) {
          reject(new Error(`HTTP ${res.statusCode} for ${u}`));
          return;
        }
        const tmp = destPath + ".tmp";
        const out = fs.createWriteStream(tmp);
        res.pipe(out);
        out.on("finish", () => {
          fs.renameSync(tmp, destPath);
          resolve();
        });
        out.on("error", (e) => {
          res.destroy();
          fs.rmSync(tmp, { force: true });
          reject(e);
        });
      }).on("error", reject);
    };
    follow(url);
  });
}

async function extractZip(zipPath, destDir) {
  // Node has no built-in zip extraction — use child_process with unzip/PowerShell
  const { execFileSync } = await import("node:child_process");
  if (process.platform === "win32") {
    const safePath = (p) => p.replace(/'/g, "''");
    execFileSync("powershell", [
      "-Command",
      `Expand-Archive -Path '${safePath(zipPath)}' -DestinationPath '${safePath(destDir)}' -Force`,
    ]);
  } else {
    execFileSync("unzip", ["-o", "-q", zipPath, "-d", destDir]);
  }
}

async function main() {
  const platformKey = getPlatformKey();
  if (!platformKey || !PLATFORM_MAP[platformKey]) {
    warn(`unsupported platform ${process.platform}-${process.arch}. Supported: ${Object.keys(PLATFORM_MAP).join(", ")}`);
    warn("Falling back to system node/bun. Install bun manually: https://bun.sh/install");
    process.exit(0);
  }

  const bunBin = getBunBin();
  if (fs.existsSync(bunBin)) {
    // Already downloaded
    process.exit(0);
  }

  const bunDir = getBunDir();
  fs.mkdirSync(bunDir, { recursive: true });

  const zipName = `${PLATFORM_MAP[platformKey]}.zip`;
  const url = `https://github.com/oven-sh/bun/releases/download/bun-v${BUN_VERSION}/${zipName}`;
  const zipPath = path.join(bunDir, zipName);

  process.stdout.write(`total-recall: downloading bun v${BUN_VERSION} for ${platformKey}...\n`);

  try {
    await download(url, zipPath);
    await extractZip(zipPath, bunDir);
    fs.rmSync(zipPath, { force: true });

    // The zip extracts to a subdirectory e.g. bun-linux-x64/bun — move it up
    const extractedDir = path.join(bunDir, PLATFORM_MAP[platformKey]);
    const extractedBin = path.join(extractedDir, process.platform === "win32" ? "bun.exe" : "bun");
    if (fs.existsSync(extractedBin)) {
      fs.renameSync(extractedBin, bunBin);
      fs.rmSync(extractedDir, { recursive: true, force: true });
    } else {
      throw new Error(`Could not find extracted binary at ${extractedBin}. Unexpected zip layout.`);
    }

    if (process.platform !== "win32") {
      fs.chmodSync(bunBin, 0o755);
    }

    process.stdout.write(`total-recall: bun v${BUN_VERSION} ready at ${bunBin}\n`);
  } catch (e) {
    // Clean up any partial files
    fs.rmSync(zipPath, { force: true });
    fs.rmSync(bunBin, { force: true });
    if (e.code === 'ENOENT') {
      warn("required tool not found: " + (e.path || 'unzip') + ". Install it (e.g. `apt install unzip`) and re-run `npm install`.");
    } else {
      warn(`could not download bun: ${e.message}`);
    }
    warn("Falling back to system node/bun.");
    warn("To fix manually: https://bun.sh/install");
    warn(`Or download directly to: ${bunBin}`);
    process.exit(0);
  }
}

main();
