// skills/develop/scripts/profile.js — the repository profile (spec section 15): `.github/develop.md`
// parsed and checked. Zero dependencies, ESM, `node:fs` only.
//
//   node profile.js parse <markdownFile>

import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { isLogin } from './checkpoints.js';

const HEADER = 'tr-develop/profile-v1';
const BASE_RE = /^[A-Za-z0-9][A-Za-z0-9._/-]*$/;
const TEMPLATE_VALUE_RE = /^[A-Za-z0-9][A-Za-z0-9._-]*\.(md|ya?ml)$/;
const TEMPLATE_KEY_RE = /^ticket\.template\.(feature|bug|chore)$/;
const VERIFY_INFO = 'sh verify';

function bad(key) {
  return { ok: false, error: `bad-value:${key}` };
}

// ---- fences ----

/** A CommonMark opening fence (up to three spaces of indent, three or more backticks or tildes). */
function fenceOpen(line) {
  const m = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(line);
  if (!m) return null;
  if (m[1][0] === '`' && m[2].includes('`')) return null;
  return { ch: m[1][0], len: m[1].length, info: m[2].trim() };
}

/** A closing fence: the same character, at least as long as the opening run, nothing else. */
function isFenceClose(line, open) {
  const m = /^ {0,3}(`{3,}|~{3,})[ \t]*$/.exec(line);
  return !!m && m[1][0] === open.ch && m[1].length >= open.len;
}

/** Every fenced block in document order, as its info string and content lines. */
function fencedBlocks(lines) {
  const blocks = [];
  let i = 0;
  while (i < lines.length) {
    const open = fenceOpen(lines[i]);
    if (!open) {
      i += 1;
      continue;
    }
    const body = [];
    let j = i + 1;
    while (j < lines.length && !isFenceClose(lines[j], open)) {
      body.push(lines[j]);
      j += 1;
    }
    blocks.push({ info: open.info, lines: body });
    i = j + 1;
  }
  return blocks;
}

// ---- parse ----

/**
 * The profile (spec section 15) from the text of `.github/develop.md`. The profile is the first
 * fenced `text` block whose first line is the header; unknown keys are ignored; a repeated known key
 * or a bad value is an error; each `sh verify` block is one command, in order.
 */
export function parseProfile(markdown) {
  const lines = String(markdown).replace(/^\uFEFF/, '').split(/\r?\n/);
  const blocks = fencedBlocks(lines);
  const block = blocks.find((b) => b.info === 'text' && (b.lines[0] ?? '').trim() === HEADER);
  if (!block) return { ok: false, error: 'no-profile' };

  const profile = { base: null, ticketStandard: 'on', templates: {}, ghAccount: null, verify: [] };
  const seen = new Set();
  for (const line of block.lines.slice(1)) {
    const colon = line.indexOf(':');
    if (colon < 0) continue;
    const key = line.slice(0, colon).trim();
    const value = line.slice(colon + 1).trim();
    const template = TEMPLATE_KEY_RE.exec(key);
    const known = key === 'base' || key === 'ticket.standard' || key === 'gh.account' || template;
    if (!known) continue;
    if (seen.has(key)) return bad(key);
    seen.add(key);

    if (key === 'base') {
      if (!BASE_RE.test(value)) return bad(key);
      profile.base = value;
    } else if (key === 'ticket.standard') {
      if (value !== 'on' && value !== 'off') return bad(key);
      profile.ticketStandard = value;
    } else if (key === 'gh.account') {
      if (!isLogin(value)) return bad(key);
      profile.ghAccount = value;
    } else {
      if (!TEMPLATE_VALUE_RE.test(value)) return bad(key);
      profile.templates[template[1]] = value;
    }
  }

  for (const b of blocks) {
    if (b.info !== VERIFY_INFO) continue;
    const command = b.lines.join('\n').trim();
    if (command === '') return bad('verify');
    profile.verify.push(command);
  }
  return { ok: true, profile };
}

// ---- CLI ----

export const SUBCOMMANDS = ['parse'];

function usage(text) {
  throw new Error(`usage: ${text}`);
}

function runParse(args) {
  const [markdownFile] = args;
  if (!markdownFile) usage('parse <markdownFile>');
  return parseProfile(fs.readFileSync(markdownFile, 'utf8'));
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
