// skills/develop/scripts/args.js — argument parsing for `/develop [arg]` (spec section 5). Zero
// dependencies, ESM, `node:fs` only.
//
//   node args.js parse <originRepo> <textFile>

import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

// GitHub numbers issues and pull requests in one sequence; nine digits at most keeps every number
// exactly representable.
const NUMBER = '[1-9]\\d{0,8}';
const HASH_RE = new RegExp(`^#?(${NUMBER})$`);
const NAME = '[A-Za-z0-9_.-]+';
const REF_RE = new RegExp(`^(${NAME})/(${NAME})#(${NUMBER})$`);
const ISSUE_URL_RE = new RegExp(`^https://github\\.com/(${NAME})/(${NAME})/issues/(${NUMBER})(?:[?#].*)?$`);
const PULL_URL_RE = new RegExp(`^https://github\\.com/(${NAME})/(${NAME})/pull/(${NUMBER})(?:[?#].*)?$`);
const REPO_RE = new RegExp(`^${NAME}/${NAME}$`);
const TUTORIAL_WORDS = ['', 'help', '--help', '-h', '?'];

/** An issue in this repository, or in another one (the caller says which repository to launch in). */
function issueIn(owner, repo, number, originRepo) {
  const named = `${owner}/${repo}`;
  if (named.toLowerCase() === originRepo.toLowerCase()) return { route: 'issue', number };
  return { route: 'wrong-repo', repo: named, number };
}

/**
 * The route for the text given to `/develop` (spec section 5). Whitespace is trimmed first. Anything
 * that is not an issue, pull request, tutorial or initialize request is a Define seeded with the
 * trimmed text.
 */
export function parseArg(text, originRepo) {
  if (typeof originRepo !== 'string' || !REPO_RE.test(originRepo)) throw new Error('originRepo must be owner/repo');
  const trimmed = String(text).trim();
  const lower = trimmed.toLowerCase();
  if (TUTORIAL_WORDS.includes(lower)) return { route: 'tutorial' };
  if (lower === 'initialize') return { route: 'initialize' };

  let m = HASH_RE.exec(trimmed);
  if (m) return { route: 'issue', number: Number(m[1]) };
  m = REF_RE.exec(trimmed);
  if (m) return issueIn(m[1], m[2], Number(m[3]), originRepo);
  m = ISSUE_URL_RE.exec(trimmed);
  if (m) return issueIn(m[1], m[2], Number(m[3]), originRepo);
  m = PULL_URL_RE.exec(trimmed);
  if (m) return { route: 'pull', number: Number(m[3]) };
  return { route: 'define', seed: trimmed };
}

// ---- CLI ----

export const SUBCOMMANDS = ['parse'];

function usage(text) {
  throw new Error(`usage: ${text}`);
}

function runParse(args) {
  const [originRepo, textFile] = args;
  if (!originRepo || !textFile) usage('parse <originRepo> <textFile>');
  return parseArg(fs.readFileSync(textFile, 'utf8'), originRepo);
}

function dispatch(argv) {
  const [cmd, ...rest] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'parse': return runParse(rest);
    default: return { ok: false, error: 'unknown subcommand' };
  }
}

function main() {
  const argv = process.argv.slice(2);
  let result;
  try {
    result = dispatch(argv);
  } catch (err) {
    result = { ok: false, error: err.message };
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
  if (result && result.ok === false) process.exitCode = 1;
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

if (isMain(import.meta.url)) main();
