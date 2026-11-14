// Shared helper: the package spec the install script writes into other tools'
// configs, and what to do with an entry that is already there.
//
// An installed package pins its own version (`@strvmarv/total-recall@4.8.0`), so
// the entry starts the release that was just installed and not an older copy that
// `npx` would otherwise prefer. A git checkout, or a package whose version is not
// a valid version, writes the bare name as before, because the version may not be
// published (and a checkout is never refreshed).
//
// "Own" entries are the exact form this script writes: command `npx`, arguments
// `--yes` and the package name, bare or with a valid version. Only those are
// refreshed. The version is checked with `isValidVersion` (the release tooling's
// check), which also keeps shell characters out of the Gemini hook command.

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isRepoSelf } from './is-repo-self.js';
import { isValidVersion } from '../sync-version.js';

export const PACKAGE_NAME = '@strvmarv/total-recall';

/** The version in this package's package.json, or 'unknown'. */
export function packageVersion() {
  try {
    const here = dirname(fileURLToPath(import.meta.url));
    const v = JSON.parse(readFileSync(join(here, '..', '..', 'package.json'), 'utf8')).version;
    return typeof v === 'string' && v ? v : 'unknown';
  } catch {
    return 'unknown';
  }
}

function defaults(opts) {
  return {
    version: opts && 'version' in opts ? opts.version : packageVersion(),
    repoSelf: opts && 'repoSelf' in opts ? opts.repoSelf : isRepoSelf(),
  };
}

/** `@strvmarv/total-recall@V` for an installed package with a valid version, else the bare name. */
export function pinnedSpec(opts) {
  const { version, repoSelf } = defaults(opts);
  return !repoSelf && isValidVersion(version) ? `${PACKAGE_NAME}@${version}` : PACKAGE_NAME;
}

/** The MCP entry the script writes: command, args, then any extra keys (Kiro's cwd). */
export function npxEntry(extra = {}, opts) {
  return { command: 'npx', args: ['--yes', pinnedSpec(opts)], ...extra };
}

function isOwnSpec(spec) {
  if (spec === PACKAGE_NAME) return true;
  return typeof spec === 'string' && spec.startsWith(`${PACKAGE_NAME}@`) && isValidVersion(spec.slice(PACKAGE_NAME.length + 1));
}

/** True for exactly `npx --yes <name>` or `npx --yes <name>@<valid version>`; other keys do not matter. */
export function isOwnNpxEntry(entry) {
  return !!entry && entry.command === 'npx' && Array.isArray(entry.args) &&
    entry.args.length === 2 && entry.args[0] === '--yes' && isOwnSpec(entry.args[1]);
}

/** The Gemini CLI BeforeAgent hook command. */
export function hookCommand(opts) {
  return `npx --yes ${pinnedSpec(opts)} pinned-floor --host gemini-cli`;
}

const HOOK_RE = /^npx --yes (\S+) pinned-floor --host gemini-cli$/;

/** True for the hook command above, with the bare name or a valid version. */
export function isOwnHookCommand(command) {
  if (typeof command !== 'string') return false;
  const m = HOOK_RE.exec(command);
  return m !== null && isOwnSpec(m[1]);
}

/**
 * What to do with an existing `total-recall` entry when the script would write `spec`:
 *   'refresh' — the script's own form with another spec, and `spec` carries a version
 *   'keep'    — already current, or an `npx` entry that holds the bare name among other arguments
 *   'custom'  — anything else (another command or package, a spec that is not a valid version)
 */
export function decide(entry, spec) {
  if (!entry || entry.command !== 'npx' || !Array.isArray(entry.args)) return 'custom';
  if (isOwnNpxEntry(entry)) return spec !== PACKAGE_NAME && entry.args[1] !== spec ? 'refresh' : 'keep';
  return entry.args.includes(PACKAGE_NAME) ? 'keep' : 'custom';
}
