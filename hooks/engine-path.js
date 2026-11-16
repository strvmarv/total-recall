// hooks/engine-path.js — where the Claude Code hooks find the engine (#98).
//
// Run by hooks/user-prompt-submit/run.sh as `node engine-path.js`.
// It prints the absolute path of the engine binary on one line (forward slashes, which Git Bash on
// Windows accepts), or prints nothing when the engine is not provisioned yet. The hooks run that
// engine directly and never `bin/start.js`: start.js provisions, which would download the engine
// inside a blocking hook, with no lock, while the MCP shim may be writing the same files.
//
// The check is pi/floor.js `resolveEngine`: a supported platform, the binary present and the model
// payload intact. It only reads. It is the check the shim's own fast path makes, so it shares its
// limit: with models/registry.json absent the payload counts as intact.
//
// Contract: this process NEVER fails a hook. It exits 0, writes nothing to stderr, and any problem
// (a missing or broken pi/floor.js included) prints nothing. pi/floor.js is loaded by dynamic import
// inside a try, so a broken file cannot raise a link-time error (pattern: hooks/session-start/whats-new.js).

import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

async function main() {
  const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  let line = '';
  try {
    const floor = await import(pathToFileURL(path.join(rootDir, 'pi', 'floor.js')).href);
    const engine = floor.resolveEngine();
    if (typeof engine === 'string' && engine !== '') line = `${engine.replaceAll('\\', '/')}\n`;
  } catch {
    line = '';
  }
  if (line === '') process.exit(0);
  process.stdout.on('error', () => process.exit(0));
  process.stdout.write(line, () => process.exit(0));
}

main().catch(() => process.exit(0));
