import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  sha256File, getManifestUrl, verifyArchiveChecksum, payloadIntact,
  resolveDataDir, storeInCache, restoreFromCache,
} from './fetch-binary.js';

// --- payloadIntact: cheap size-based integrity check of the extracted model ---
// tree against the registry that ships in it. This is the guard that catches an
// interrupted/truncated extraction (the 1.5MB-vs-133MB model.onnx bug) which the
// archive-sha check cannot see because it validates the tarball, not the tree.
function makeTree(modelBytes, { sizeBytes = 64, withRegistry = true } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-payload-'));
  const modelDir = path.join(dir, 'models', 'bge-small-en-v1.5');
  fs.mkdirSync(modelDir, { recursive: true });
  fs.writeFileSync(path.join(modelDir, 'model.onnx'), Buffer.alloc(modelBytes));
  if (withRegistry) {
    fs.writeFileSync(path.join(dir, 'models', 'registry.json'), JSON.stringify({
      version: 1,
      models: { 'bge-small-en-v1.5': { dimensions: 384, sizeBytes } },
    }));
  }
  return dir;
}

test('payloadIntact: model.onnx matching registry sizeBytes is intact', () => {
  const dir = makeTree(64, { sizeBytes: 64 });
  assert.equal(payloadIntact(dir).ok, true);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: truncated model.onnx (size mismatch) is NOT intact', () => {
  const dir = makeTree(10, { sizeBytes: 133093490 }); // extracted 10 bytes of a 133MB model
  const r = payloadIntact(dir);
  assert.equal(r.ok, false);
  assert.match(r.reason, /size/i);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: missing model.onnx is NOT intact', () => {
  const dir = makeTree(64, { sizeBytes: 64 });
  fs.rmSync(path.join(dir, 'models', 'bge-small-en-v1.5', 'model.onnx'));
  assert.equal(payloadIntact(dir).ok, false);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: present-but-corrupt registry.json is NOT intact', () => {
  // A registry that exists but does not parse is itself a signature of an
  // interrupted extraction (the file was cut mid-write). Treating it as "no
  // registry -> ok" would defeat the self-heal guard, so it must be rejected —
  // distinct from a genuinely-absent registry (ENOENT), which stays intact.
  const dir = makeTree(64, { sizeBytes: 64 });
  fs.writeFileSync(path.join(dir, 'models', 'registry.json'), '{ "version": 1, "models": {'); // truncated JSON
  const r = payloadIntact(dir);
  assert.equal(r.ok, false);
  assert.match(r.reason, /registry/i);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: no registry present is treated as intact (non-model / legacy trees)', () => {
  // Absence of a registry is not evidence of corruption — some trees have no
  // model payload to validate. Must NOT false-positive into a re-download loop.
  const dir = makeTree(64, { withRegistry: false });
  assert.equal(payloadIntact(dir).ok, true);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('sha256File computes the hex digest of a file', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-sha-'));
  const f = path.join(dir, 'data.bin');
  fs.writeFileSync(f, 'hello total-recall');
  const expected = crypto.createHash('sha256').update('hello total-recall').digest('hex');
  assert.equal(await sha256File(f), expected);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('getManifestUrl points at the versioned release asset', () => {
  assert.equal(
    getManifestUrl('3.2.0'),
    'https://github.com/strvmarv/total-recall/releases/download/v3.2.0/provisioning.manifest.json');
});

// verifyArchiveChecksum is the security-critical gate ensureBinary runs before
// extraction. Testing it directly keeps the suite hermetic (no network) while
// actually exercising the match / mismatch / read-failure contract. The full
// download+extract path is covered offline in the golden shim test.
test('verifyArchiveChecksum: matching digest returns null', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-vsum-'));
  const f = path.join(dir, 'a.tar.gz');
  fs.writeFileSync(f, 'archive-bytes');
  const good = crypto.createHash('sha256').update('archive-bytes').digest('hex');
  assert.equal(await verifyArchiveChecksum(f, good.toUpperCase(), 'a.tar.gz'), null); // case-insensitive
  fs.rmSync(dir, { recursive: true, force: true });
});

test('verifyArchiveChecksum: mismatch flags checksumMismatch (non-retryable)', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-vsum-'));
  const f = path.join(dir, 'a.tar.gz');
  fs.writeFileSync(f, 'archive-bytes');
  const bad = await verifyArchiveChecksum(f, 'deadbeef', 'a.tar.gz');
  assert.equal(bad.ok, false);
  assert.equal(bad.checksumMismatch, true);
  assert.match(bad.error, /checksum mismatch/i);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('verifyArchiveChecksum: read failure is a plain error, NOT checksumMismatch', async () => {
  const r = await verifyArchiveChecksum(path.join(os.tmpdir(), 'tr-does-not-exist-xyz'), 'deadbeef');
  assert.equal(r.ok, false);
  assert.notEqual(r.checksumMismatch, true); // a missing file may be retried; a mismatch may not
  assert.match(r.error, /checksum read failed/i);
});


// --- the cache of verified engine archives (issue #65) ---
//
// The helpers take the data directory, the destination and every side-effecting step as arguments,
// so these tests run over temp directories and never touch the real plugin tree.

const VERSION = '4.6.0';
const ARCHIVE = 'total-recall-linux-x64.tar.gz';
const isWin = process.platform === 'win32';

function tmpDir(prefix = 'tr-cache-') {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

function makeArchive(content = 'engine archive bytes') {
  const dir = tmpDir('tr-archive-');
  const file = path.join(dir, ARCHIVE);
  fs.writeFileSync(file, content);
  return { file, sha256: crypto.createHash('sha256').update(content).digest('hex') };
}

/** A restore target with spies for every injected step; `extract` unpacks a fake engine tree. */
function makeRestore(overrides = {}) {
  const destDir = tmpDir('tr-dest-');
  const binaryPath = path.join(destDir, 'total-recall');
  const calls = { extract: 0, chmod: [], marker: [] };
  return {
    destDir, binaryPath, calls,
    args: {
      version: VERSION, archiveName: ARCHIVE, destDir, binaryPath,
      extract: (archivePath, dir) => {
        calls.extract++;
        fs.writeFileSync(path.join(dir, 'total-recall'), 'binary');
      },
      payloadIntact: () => ({ ok: true }),
      chmod: (p, mode) => { calls.chmod.push([p, mode]); },
      writeMarker: (sha) => { calls.marker.push(sha); },
      platform: 'linux',
      ...overrides,
    },
  };
}

const store = (dataDir, archive, extra = {}) =>
  storeInCache({ dataDir, version: VERSION, archiveName: ARCHIVE, archivePath: archive.file, sha256: archive.sha256, ...extra });

test('resolveDataDir: a non-empty TOTAL_RECALL_HOME is used as it is', () => {
  const home = path.resolve(path.sep, 'x');
  assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: home, HOME: path.resolve(path.sep, 'h') }), home);
});

test('resolveDataDir: otherwise .total-recall under HOME, then USERPROFILE', () => {
  const h = path.resolve(path.sep, 'h');
  const u = path.resolve(path.sep, 'u');
  assert.equal(resolveDataDir({ HOME: h }), path.join(h, '.total-recall'));
  assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: '', HOME: h, USERPROFILE: u }), path.join(h, '.total-recall'));
  assert.equal(resolveDataDir({ USERPROFILE: u }), path.join(u, '.total-recall'));
});

test('resolveDataDir: unknown or relative locations give null, so the cache is skipped', () => {
  assert.equal(resolveDataDir({}), null);
  assert.equal(resolveDataDir({ HOME: '' , USERPROFILE: path.resolve(path.sep, 'u') }), null, 'an empty HOME wins over USERPROFILE, as in the engine, and is relative');
  assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: 'relative/dir', HOME: path.resolve(path.sep, 'h') }), null);
  assert.equal(resolveDataDir({ HOME: 'relative' }), null);
});

test('cache: a stored archive is restored without downloading, executable, with its marker', async () => {
  const dataDir = tmpDir();
  const archive = makeArchive();
  assert.equal(store(dataDir, archive), true);
  const t = makeRestore();
  const r = await restoreFromCache({ dataDir, ...t.args });
  assert.equal(r.restored, true);
  assert.ok(fs.existsSync(t.binaryPath));
  assert.deepEqual(t.calls.chmod, [[t.binaryPath, 0o755]]);
  assert.deepEqual(t.calls.marker, [archive.sha256]);
  // the cache keeps the entry for the next reinstall
  assert.equal((await restoreFromCache({ dataDir, ...makeRestore().args })).restored, true);
});

test('cache: tampered bytes no longer match the recorded sha256, so the entry is deleted', async () => {
  const dataDir = tmpDir();
  store(dataDir, makeArchive());
  const entry = path.join(dataDir, 'cache', 'binaries', VERSION, ARCHIVE);
  fs.writeFileSync(entry, 'something else');
  const t = makeRestore();
  const r = await restoreFromCache({ dataDir, ...t.args });
  assert.equal(r.restored, false);
  assert.equal(t.calls.extract, 0, 'a tampered archive is never extracted');
  assert.equal(fs.existsSync(entry), false);
  assert.equal(fs.existsSync(`${entry}.sha256`), false);
});

test('cache: an archive stored for another version is never used', async () => {
  const dataDir = tmpDir();
  store(dataDir, makeArchive(), { version: '4.5.0' });
  const t = makeRestore();
  assert.equal((await restoreFromCache({ dataDir, ...t.args })).restored, false);
  assert.equal(t.calls.extract, 0);
  assert.ok(fs.existsSync(path.join(dataDir, 'cache', 'binaries', '4.5.0', ARCHIVE)), 'the other version is untouched');
});

test('cache: an entry with no sidecar checksum is a miss that leaves the entry alone', async () => {
  const dataDir = tmpDir();
  store(dataDir, makeArchive());
  const entry = path.join(dataDir, 'cache', 'binaries', VERSION, ARCHIVE);
  fs.rmSync(`${entry}.sha256`);
  const t = makeRestore();
  assert.equal((await restoreFromCache({ dataDir, ...t.args })).restored, false);
  assert.equal(t.calls.extract, 0);
  assert.ok(fs.existsSync(entry));
});

test('cache: a cache directory owned by someone else is not used and nothing is stored in it', async () => {
  const dataDir = tmpDir();
  const archive = makeArchive();
  store(dataDir, archive);
  const foreign = { lstat: (p) => ({ ...fs.lstatSync(p), isSymbolicLink: () => false, isDirectory: () => true, uid: 4242, mode: 0o40700 }), uid: 1000 };
  const t = makeRestore();
  assert.equal((await restoreFromCache({ dataDir, ...t.args, statOps: foreign })).restored, false);
  assert.equal(t.calls.extract, 0);
  assert.equal(store(dataDir, archive, { statOps: foreign }), false);
});

test('cache: a symbolic-link or group/other-writable cache directory is not used', async () => {
  const dataDir = tmpDir();
  store(dataDir, makeArchive());
  const mine = (over) => ({ lstat: (p) => ({ isSymbolicLink: () => false, isDirectory: () => true, uid: 1000, mode: 0o40700, ...over }), uid: 1000 });
  for (const over of [{ isSymbolicLink: () => true }, { mode: 0o40770 }, { mode: 0o40707 }, { mode: 0o40777 }]) {
    const t = makeRestore();
    assert.equal((await restoreFromCache({ dataDir, ...t.args, statOps: mine(over) })).restored, false, JSON.stringify(Object.keys(over)));
    assert.equal(t.calls.extract, 0);
  }
  // sanity: the same stat shape without the problem is trusted
  assert.equal((await restoreFromCache({ dataDir, ...makeRestore().args, statOps: mine({}) })).restored, true);
});

test('cache: an unusable data directory skips the cache without throwing', async () => {
  const archive = makeArchive();
  assert.equal(store(null, archive), false);
  assert.equal(store('relative/dir', archive), false);
  const t = makeRestore();
  assert.equal((await restoreFromCache({ dataDir: null, ...t.args })).restored, false);
});

test('cache: a data directory that does not exist yet is created, and only cache/ is added to it', async () => {
  const parent = tmpDir();
  const dataDir = path.join(parent, 'fresh', '.total-recall');
  assert.equal(store(dataDir, makeArchive()), true);
  assert.deepEqual(fs.readdirSync(dataDir), ['cache']);
  assert.equal((await restoreFromCache({ dataDir, ...makeRestore().args })).restored, true);
});

test('cache: version and archive names that would leave the cache directory are refused', async () => {
  const dataDir = tmpDir();
  const archive = makeArchive();
  assert.equal(store(dataDir, archive, { version: '../evil' }), false);
  assert.equal(store(dataDir, archive, { archiveName: '../evil.tar.gz' }), false);
  const t = makeRestore({ version: '../evil' });
  assert.equal((await restoreFromCache({ dataDir, ...t.args })).restored, false);
  assert.equal(fs.existsSync(path.join(dataDir, 'evil')), false);
});
