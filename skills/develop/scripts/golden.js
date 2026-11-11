import fs from 'node:fs';

const OPEN_RE = /^```json golden:([A-Za-z0-9_-]+)\s*$/;
const CLOSE_RE = /^```\s*$/;

function scan(markdownPath) {
  const lines = fs.readFileSync(markdownPath, 'utf8').split('\n').map((l) => l.replace(/\r$/, ''));
  const blocks = [];
  let open = null;
  let buf = [];
  for (const line of lines) {
    if (open === null) {
      const m = OPEN_RE.exec(line);
      if (m) {
        open = m[1];
        buf = [];
      }
      continue;
    }
    if (CLOSE_RE.test(line)) {
      blocks.push({ name: open, text: buf.join('\n') });
      open = null;
      buf = [];
      continue;
    }
    buf.push(line);
  }
  return blocks;
}

export function goldenNames(markdownPath) {
  return scan(markdownPath).map((b) => b.name);
}

export function readGolden(markdownPath, name) {
  const matches = scan(markdownPath).filter((b) => b.name === name);
  if (matches.length === 0) {
    throw new Error(`golden ${name} not found`);
  }
  if (matches.length > 1) {
    throw new Error(`golden ${name} duplicated`);
  }
  try {
    return JSON.parse(matches[0].text);
  } catch (err) {
    throw new Error(`golden ${name} is not valid JSON: ${err.message}`);
  }
}
