import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { goldenNames, readGolden } from './golden.js';
import { parseArg } from './args.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SKILL_DIR = path.join(HERE, '..');
const REPO = path.join(SKILL_DIR, '..', '..');
const AGENTS_DIR = path.join(REPO, 'agents');
const ORIGIN = 'strvmarv/total-recall';

// Task 18 sets this to true once every skill file exists; it activates the coverage assertions.
const COMPLETE = false;

// Later tasks add one entry each. Every listed `## ` heading must be present, every listed golden
// must parse, and every relative link in a listed file must resolve.
const FILES = {
  'SKILL.md': {
    headings: [
      'Routing',
      'Issue entry',
      'Hosts',
      'Preflight',
      'Overrides of superpowers defaults',
      'Worktrees',
      'Progress',
      'Memory',
      'Commits and PRs',
    ],
    goldens: ['routing', 'pinned-commands'],
  },
  'ticket-standard.md': {
    headings: [
      'Is it one ticket',
      'Problem before solution',
      'Acceptance criteria',
      'Scope',
      'Ready',
      'Shape by type',
      'Title, labels and type',
      'Repository overrides',
      'Review grades',
      'Markers',
    ],
    goldens: [],
  },
  'handoff.md': {
    headings: [
      'Hashing and regions',
      'The issue after Define',
      'Checkpoints',
      'Trust',
      'Resume and routing',
      'Re-approval',
      'After the PR',
    ],
    goldens: [
      'normalize-pairs', 'body-pairs', 'region-cases', 'fence-cases', 'limit-cases', 'neutralize-cases',
      'parse-cases', 'validate-cases', 'carry-cases', 'readback-cases',
      'perm-cases', 'login-cases', 'comment-count-cases', 'pin-cases', 'flatten-cases', 'flatten-fail-cases',
      'fetch-commands', 'pages-cases', 'history-cases', 'write-access-cases', 'select-cases',
      'position-cases', 'task-count-cases', 'tasks-for-cases', 'live-cases', 'route-cases',
    ],
  },
  'define.md': {
    headings: [
      'Fetch',
      'Claim',
      'Worktree',
      'Brainstorm',
      'Plan track',
      'Small track',
      'Sign-off',
      'Publish',
      'Wrap-up',
      'Rules',
    ],
    goldens: [],
  },
  'deliver.md': {
    headings: [
      'Common steps',
      'Plan track',
      'Small track',
      'Finish and close-out',
      'Merged close-out',
    ],
    goldens: [],
  },
  'review-contract.md': {
    headings: ['Dispatch frame', 'Loop rules', 'The final loop', 'Self-review mode', 'Agents'],
    goldens: [],
  },
  'blockers.md': {
    headings: ['Stop for these', 'Decide and continue', 'At a blocker'],
    goldens: [],
  },
  'tutorial.md': {
    h1: '# develop',
    maxLines: 60,
    headings: [],
    goldens: [],
  },
};

// Scripts that are libraries, not CLIs: no SUBCOMMANDS export, never called from a shell.
const LIBRARIES = ['golden.js', 'pages.js'];

// Whole-token match: the word after a CLI script name must be exactly a subcommand of that script.
const CLI_RE = /\b(regions|checkpoints|counted|history|route|args|ghpin|profile)\.js[ \t]+([A-Za-z][\w-]*)(?![\w-])/g;

// Task 18's home table: subcommands whose mention must appear in the named file.
const HOME = [
  ['SKILL.md', 'ghpin', ['scratch', 'pick', 'command', 'run']],
  ['SKILL.md', 'args', ['parse']],
  ['define.md', 'regions', ['neutralize', 'fence']],
  ['handoff.md', 'regions', ['body-json', 'body', 'ac', 'approach']],
  ['handoff.md', 'checkpoints', ['validate', 'carry', 'format', 'readback']],
  ['handoff.md', 'counted', ['select', 'write-access', 'permissions']],
  ['handoff.md', 'history', ['find']],
  ['deliver.md', 'route', ['route', 'live', 'position', 'count-tasks', 'count-cycles', 'tasks-for']],
  ['profile.md', 'profile', ['parse']],
];

// Forbidden provenance strings are built from fragments so this file never contains them.
const FORBIDDEN = [
  'cor' + 'tex',
  'Ji' + 'ra',
  'AIO' + 'PS',
  'rai' + '-ops',
  'Rada' + 'ncy',
  '{no' + 'format}',
  'STR' + 'ATA',
];

// ---- helpers ----

function read(rel) {
  return fs.readFileSync(path.join(SKILL_DIR, rel), 'utf8').replace(/\r\n/g, '\n');
}

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else out.push(full);
  }
  return out;
}

function skillMarkdown() {
  return walk(SKILL_DIR).filter((f) => f.endsWith('.md'));
}

/** The lines of `text` that are outside fenced code blocks. */
function outsideFences(text) {
  const out = [];
  let fence = null;
  for (const line of text.split('\n')) {
    const m = /^ {0,3}(`{3,}|~{3,})/.exec(line);
    if (fence === null) {
      if (m) fence = m[1];
      else out.push(line);
    } else if (m && m[1][0] === fence[0] && m[1].length >= fence.length && /^ {0,3}[`~]+\s*$/.test(line)) {
      fence = null;
    }
  }
  return out;
}

function headings(text) {
  return outsideFences(text)
    .map((l) => /^## (.+?)\s*$/.exec(l))
    .filter(Boolean)
    .map((m) => m[1]);
}

/** The text of one `## ` section, up to the next `## ` heading. */
function section(text, name) {
  const lines = text.split('\n');
  const start = lines.findIndex((l) => l === `## ${name}`);
  assert.ok(start >= 0, `section ${name} exists`);
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i++) {
    if (/^## /.test(lines[i])) {
      end = i;
      break;
    }
  }
  return lines.slice(start + 1, end).join('\n');
}

async function scriptExports() {
  const scripts = fs.readdirSync(HERE).filter((f) => f.endsWith('.js') && !f.endsWith('.test.js'));
  const result = {};
  for (const file of scripts) {
    result[file] = await import(pathToFileURL(path.join(HERE, file)).href);
  }
  return result;
}

// ---- FILES table ----

for (const [file, spec] of Object.entries(FILES)) {
  test(`${file}: exists`, () => {
    assert.ok(fs.existsSync(path.join(SKILL_DIR, file)), `${file} exists`);
  });

  test(`${file}: required headings`, () => {
    const found = headings(read(file));
    for (const h of spec.headings) {
      assert.ok(found.includes(h), `${file} has "## ${h}"`);
    }
  });

  test(`${file}: required goldens parse`, () => {
    for (const name of spec.goldens) {
      assert.ok(goldenNames(path.join(SKILL_DIR, file)).includes(name), `${file} has golden ${name}`);
      assert.doesNotThrow(() => readGolden(path.join(SKILL_DIR, file), name), `${file} golden ${name} parses`);
    }
  });

  test(`${file}: no golden is defined twice`, () => {
    const names = goldenNames(path.join(SKILL_DIR, file));
    assert.equal(new Set(names).size, names.length, `${file} golden names are unique: ${names.join(', ')}`);
  });

  test(`${file}: every relative link resolves`, () => {
    const text = outsideFences(read(file)).join('\n');
    const re = /\]\(([^)\s]+)\)/g;
    let m;
    while ((m = re.exec(text)) !== null) {
      const target = m[1];
      if (/^([a-z][a-z0-9+.-]*:|#)/i.test(target)) continue;
      const rel = decodeURIComponent(target.split('#')[0]);
      assert.ok(fs.existsSync(path.join(SKILL_DIR, path.dirname(file), rel)), `${file}: link ${target} resolves`);
    }
  });

  if (spec.h1 !== undefined) {
    test(`${file}: starts with ${spec.h1}`, () => {
      assert.equal(read(file).split('\n')[0], spec.h1);
    });
  }

  if (spec.maxLines !== undefined) {
    test(`${file}: at most ${spec.maxLines} lines`, () => {
      const text = read(file).replace(/\n$/, '');
      assert.ok(text.split('\n').length <= spec.maxLines, `${file} has ${text.split('\n').length} lines`);
    });
  }
}

// ---- SKILL.md frontmatter and goldens ----

test('SKILL.md frontmatter: name develop, description starts "Use when"', () => {
  const m = /^---\n([\s\S]*?)\n---\n/.exec(read('SKILL.md'));
  assert.ok(m, 'frontmatter block');
  const front = {};
  for (const line of m[1].split('\n')) {
    const kv = /^([a-z]+):\s*(.*)$/.exec(line);
    if (kv) front[kv[1]] = kv[2];
  }
  assert.equal(front.name, 'develop');
  assert.ok(/^Use when/.test(front.description ?? ''), 'description starts with "Use when"');
});

test('every routing golden in SKILL.md passes parseArg', () => {
  const skill = path.join(SKILL_DIR, 'SKILL.md');
  const names = goldenNames(skill).filter((n) => n === 'routing');
  assert.equal(names.length, 1, 'one routing golden');
  const cases = readGolden(skill, 'routing');
  assert.ok(cases.length > 0);
  for (const [input, expected] of cases) {
    assert.deepEqual(parseArg(input, ORIGIN), expected, `routing case ${JSON.stringify(input)}`);
  }
});

// ---- the CLI / prose contract ----

test('every CLI script exports SUBCOMMANDS; the libraries do not', async () => {
  const mods = await scriptExports();
  for (const lib of LIBRARIES) {
    assert.ok(lib in mods, `library ${lib} exists`);
    assert.equal(mods[lib].SUBCOMMANDS, undefined, `${lib} is a library and exports no SUBCOMMANDS`);
  }
  const clis = Object.keys(mods).filter((f) => !LIBRARIES.includes(f));
  assert.ok(clis.length >= 8, `at least eight CLIs, found ${clis.join(', ')}`);
  for (const file of clis) {
    const subs = mods[file].SUBCOMMANDS;
    assert.ok(Array.isArray(subs) && subs.length > 0, `${file} exports a non-empty SUBCOMMANDS array`);
    for (const s of subs) assert.match(s, /^[a-z][a-z-]*$/, `${file} subcommand ${s}`);
    assert.equal(new Set(subs).size, subs.length, `${file} SUBCOMMANDS has no duplicates`);
  }
});

test('every "<script>.js <word>" in a skill Markdown file names a real subcommand', async () => {
  const mods = await scriptExports();
  for (const file of skillMarkdown()) {
    const text = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
    for (const m of text.matchAll(CLI_RE)) {
      const subs = mods[`${m[1]}.js`].SUBCOMMANDS;
      assert.ok(subs.includes(m[2]), `${path.relative(SKILL_DIR, file)}: "${m[0]}" is not a subcommand of ${m[1]}.js (${subs.join(', ')})`);
    }
  }
});

test('CLI_RE is a whole-token match', () => {
  const hit = (s) => [...s.matchAll(CLI_RE)].map((m) => m[2]);
  assert.deepEqual(hit('regions.js body-json x'), ['body-json']);
  assert.deepEqual(hit('regions.js body-jsonx'), ['body-jsonx']);
  assert.deepEqual(hit('golden.js and pages.js and pi/develop.js and Node.js is'), []);
  assert.deepEqual(hit('ghpin.js Run'), ['Run']);
  assert.deepEqual(hit('counted.js write_access'), ['write_access']);
});

test('coverage: every subcommand of every CLI is mentioned (active when COMPLETE)', { skip: !COMPLETE }, async () => {
  const mods = await scriptExports();
  const seen = new Set();
  for (const file of skillMarkdown()) {
    for (const m of fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n').matchAll(CLI_RE)) seen.add(`${m[1]}.js ${m[2]}`);
  }
  for (const [file, mod] of Object.entries(mods)) {
    if (LIBRARIES.includes(file)) continue;
    for (const s of mod.SUBCOMMANDS) {
      assert.ok(seen.has(`${file.replace(/\.js$/, '')}.js ${s}`), `${file} ${s} is mentioned in a skill Markdown file`);
    }
  }
});

test('coverage: each home-table subcommand is mentioned in its file (active when COMPLETE)', { skip: !COMPLETE }, () => {
  for (const [file, script, subs] of HOME) {
    const text = read(file);
    const found = new Set([...text.matchAll(CLI_RE)].filter((m) => m[1] === script).map((m) => m[2]));
    for (const s of subs) assert.ok(found.has(s), `${file} mentions ${script}.js ${s}`);
  }
});

// ---- provenance ----

test('no file under skills/develop or in the two reviewer agents has a forbidden provenance string', () => {
  const files = [
    ...walk(SKILL_DIR),
    path.join(AGENTS_DIR, 'adversarial-reviewer.md'),
    path.join(AGENTS_DIR, 'counterexample-reviewer.md'),
  ];
  for (const file of files) {
    const text = fs.readFileSync(file, 'utf8').toLowerCase();
    for (const word of FORBIDDEN) {
      assert.ok(!text.includes(word.toLowerCase()), `${path.relative(REPO, file)} contains a forbidden provenance string`);
    }
  }
});

// ---- content of the sections this task writes ----

test('SKILL.md: the Preflight section holds the pinned command forms and the CLI rules', () => {
  const pre = section(read('SKILL.md'), 'Preflight');
  assert.ok(pre.includes('golden:pinned-commands'), 'Preflight carries the pinned-commands golden');
  for (const name of ['regions.js', 'checkpoints.js', 'counted.js', 'history.js', 'route.js', 'args.js', 'ghpin.js', 'profile.js']) {
    assert.ok(pre.includes(name), `Preflight names ${name}`);
  }
  assert.ok(pre.includes('golden.js') && pre.includes('pages.js'), 'Preflight names the two libraries');
  for (const term of ['ask_user', 'update_progress', 'Agent', 'cd <dir> &&']) {
    assert.ok(pre.includes(term), `Preflight names the pi mapping term ${term}`);
  }
  assert.ok(pre.includes('review=self'), 'Preflight covers the review=self fallback');
});

test('SKILL.md: the overrides table has O1 to O10', () => {
  const text = section(read('SKILL.md'), 'Overrides of superpowers defaults');
  for (let i = 1; i <= 10; i++) assert.match(text, new RegExp(`^\\| O${i} \\|`, 'm'), `row O${i}`);
});

test('SKILL.md: Preflight lists all eight checks in order', () => {
  const pre = section(read('SKILL.md'), 'Preflight');
  const nums = [...pre.matchAll(/^(\d)\. \*\*/gm)].map((m) => Number(m[1]));
  assert.deepEqual(nums, [1, 2, 3, 4, 5, 6, 7, 8]);
});

test('ticket-standard.md: names the practices, the template path, the marker and the approach limit', () => {
  const text = read('ticket-standard.md');
  for (const term of ['INVEST', 'Given/When/Then', '.github/ISSUE_TEMPLATE', '<!-- tr-develop:ac-begin -->', '60 lines']) {
    assert.ok(text.includes(term), `ticket-standard.md mentions ${term}`);
  }
});

test('handoff.md: the sections name the CLIs and the rulings they own', () => {
  const text = read('handoff.md');
  const checkpoints = section(text, 'Checkpoints');
  for (const term of ['checkpoints.js carry', 'checkpoints.js format', 'checkpoints.js readback']) {
    assert.ok(checkpoints.includes(term), `Checkpoints names ${term}`);
  }
  assert.ok(checkpoints.includes('before posting'), 'Checkpoints says the prior progress is taken before posting');
  const trust = section(text, 'Trust');
  assert.ok(trust.includes('scratch directory'), 'Trust names the per-run scratch directory');
  assert.ok(/resumed session is a new run/.test(trust), 'Trust says a resumed session is a new run');
  assert.ok(/Check integrity \(see Trust: Integrity\), except for a `blocked`, `defined`, `reapproved` or `closed`/.test(checkpoints.replace(/\s+/g, ' ')), 'Checkpoints runs integrity at posting, exempting blocked, defined, reapproved and closed');
  assert.ok(!checkpoints.includes('Integrity is not a posting step'), 'Checkpoints does not drop the per-checkpoint integrity check');
  assert.ok(/except a `blocked`, `defined`, `reapproved`\s+or `closed`/.test(trust), 'Trust names the same four exemptions');
  assert.ok(section(text, 'Resume and routing').includes('latest'), 'Resume names latest');
  const reapproval = section(text, 'Re-approval');
  assert.ok(reapproval.includes('history.js find'), 'Re-approval names history.js find');
  assert.ok(reapproval.includes('original unavailable'), 'Re-approval covers found:false');
  assert.ok(/\{ok:false[^}]*\}[^\n]*blocker 3/.test(reapproval), 'Re-approval stops with blocker 3 on {ok:false}');
});

test('deliver.md: the five flow sections come in the spec order', () => {
  const found = headings(read('deliver.md'));
  const order = ['Common steps', 'Plan track', 'Small track', 'Finish and close-out', 'Merged close-out'];
  const at = order.map((h) => found.indexOf(h));
  assert.ok(at.every((i) => i >= 0), 'all five sections exist');
  assert.deepEqual([...at].sort((a, b) => a - b), at, 'the five sections are in order');
});

test('deliver.md: names the CLIs, the position, the PR keywords and the rulings', () => {
  const text = read('deliver.md');
  for (const term of [
    'route.js', 'position', 'Closes #', 'Refs #', 'final-approved',
    'checkpoints.js carry', 'checkpoints.js format', 'checkpoints.js readback', 'history.js find',
    'pinValid', 'Tracks #', 'git check-ref-format --branch', 'sh -e -c', 'no AI attribution',
  ]) {
    assert.ok(text.includes(term), `deliver.md mentions ${term}`);
  }
  assert.ok(/never merge/i.test(text), 'deliver.md says never merge');
  assert.ok(!text.toLowerCase().includes('co-authored' + '-by'), 'deliver.md has no co-author trailer text');
});

test('deliver.md: carry runs with the select progress as prior before every kickoff and every reapproved', () => {
  const text = read('deliver.md').replace(/\s+/g, ' ');
  assert.ok(/checkpoints\.js carry/.test(text), 'names carry');
  assert.ok(text.includes('`prior`') && text.includes('before posting'), 'prior is the progress taken before posting');
  assert.ok(text.includes('every `kickoff` and every `reapproved`'), 'carry before every kickoff and every reapproved');
});

test('define.md: names the hashes, the comment, the original block, the retry cap and the CLIs it runs', () => {
  const text = read('define.md');
  for (const term of [
    'bodyHash', 'acHash', 'defined', 'Original report', 'at most twice', 'body-json', 'write-access',
    'checkpoints.js format', 'checkpoints.js readback',
  ]) {
    assert.ok(text.includes(term), `define.md mentions ${term}`);
  }
});

test('review-contract.md: the frame, the cap, the threshold and the required ending', () => {
  const text = read('review-contract.md');
  for (const term of ['round N of ≤ 6', '600', 'VERDICT: GO | NO-GO', 'FINDINGS:', 'at most 3 primary agents, each dispatching at most 1 sub-agent', 'review=self']) {
    assert.ok(text.includes(term), `review-contract.md mentions ${term}`);
  }
  const frame = section(text, 'Dispatch frame');
  const at = ['Scope', 'Constraints', 'Round', 'Fanout', 'Required ending'].map((w) => frame.indexOf(`**${w}`));
  assert.ok(at.every((i) => i >= 0), 'all five frame parts exist');
  assert.deepEqual([...at].sort((a, b) => a - b), at, 'the five frame parts are in order');
  assert.ok(/^1\. \*\*Scope/m.test(frame) && /^5\. \*\*Required ending/m.test(frame), 'the frame parts are numbered 1 to 5');
});

test('blockers.md: the six blockers are list items, and the procedure names its fields', () => {
  const text = read('blockers.md');
  const stop = section(text, 'Stop for these');
  const nums = [...stop.matchAll(/^(\d)\. /gm)].map((m) => Number(m[1]));
  assert.deepEqual(nums, [1, 2, 3, 4, 5, 6], 'blockers 1 to 6 are numbered list items in order');
  const at = section(text, 'At a blocker');
  for (const term of ['terminal', 'latest', 'stage', 'blocker', 'branch=-', 'head', 'counted.js select', 'BLOCKED:']) {
    assert.ok(at.includes(term), `At a blocker mentions ${term}`);
  }
  assert.ok(text.includes('terminal'), 'blockers.md names the terminal-only kinds');
});
