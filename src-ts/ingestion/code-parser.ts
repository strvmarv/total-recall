export interface CodeChunk {
  content: string;
  name: string;
  kind: "import" | "function" | "class" | "block";
  startLine: number;
  endLine: number;
}

function estimateTokens(text: string): number {
  const wordCount = text.trim().split(/\s+/).filter(Boolean).length;
  return Math.ceil(wordCount * 0.75);
}

interface LanguagePatterns {
  /** Regex to detect a boundary line that starts a new top-level construct */
  boundary: RegExp;
  /** Regex to detect an import line */
  importLine: RegExp;
  /** Extract the name from a boundary line */
  extractName: (line: string) => string;
  /** Classify the kind from a boundary line */
  classifyKind: (line: string) => "function" | "class" | "block";
}

const PATTERNS: Record<string, LanguagePatterns> = {
  typescript: {
    boundary: /^(export\s+)?(async\s+)?function\s+\w+|^(export\s+)?(abstract\s+)?class\s+\w+|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?\(|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?function/,
    importLine: /^\s*import\s/,
    extractName(line: string): string {
      const m =
        /function\s+(\w+)/.exec(line) ||
        /class\s+(\w+)/.exec(line) ||
        /const\s+(\w+)/.exec(line);
      return m ? m[1]! : "";
    },
    classifyKind(line: string): "function" | "class" | "block" {
      if (/class\s+/.test(line)) return "class";
      if (/function\s+|=\s*(async\s+)?\(|=\s*(async\s+)?function/.test(line)) return "function";
      return "block";
    },
  },
  javascript: {
    boundary: /^(export\s+)?(async\s+)?function\s+\w+|^(export\s+)?(class)\s+\w+|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?\(|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?function/,
    importLine: /^\s*import\s|^\s*const\s+\w+\s*=\s*require\(/,
    extractName(line: string): string {
      const m =
        /function\s+(\w+)/.exec(line) ||
        /class\s+(\w+)/.exec(line) ||
        /const\s+(\w+)/.exec(line);
      return m ? m[1]! : "";
    },
    classifyKind(line: string): "function" | "class" | "block" {
      if (/class\s+/.test(line)) return "class";
      if (/function\s+|=\s*(async\s+)?\(|=\s*(async\s+)?function/.test(line)) return "function";
      return "block";
    },
  },
  python: {
    boundary: /^(async\s+)?def\s+\w+|^class\s+\w+/,
    importLine: /^\s*import\s|^\s*from\s+\S+\s+import\s/,
    extractName(line: string): string {
      const m = /(?:def|class)\s+(\w+)/.exec(line);
      return m ? m[1]! : "";
    },
    classifyKind(line: string): "function" | "class" | "block" {
      if (/^class\s+/.test(line)) return "class";
      if (/(?:async\s+)?def\s+/.test(line)) return "function";
      return "block";
    },
  },
  go: {
    boundary: /^func\s+/,
    importLine: /^\s*import\s|^\s*"[\w/]+"/,
    extractName(line: string): string {
      const m = /func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)/.exec(line);
      return m ? m[1]! : "";
    },
    classifyKind(_line: string): "function" | "class" | "block" {
      return "function";
    },
  },
  rust: {
    boundary: /^(pub\s+)?(async\s+)?fn\s+\w+|^(pub\s+)?struct\s+\w+|^(pub\s+)?impl\s+\w+/,
    importLine: /^\s*use\s/,
    extractName(line: string): string {
      const m =
        /fn\s+(\w+)/.exec(line) ||
        /struct\s+(\w+)/.exec(line) ||
        /impl\s+(\w+)/.exec(line);
      return m ? m[1]! : "";
    },
    classifyKind(line: string): "function" | "class" | "block" {
      if (/struct\s+/.test(line) || /impl\s+/.test(line)) return "class";
      if (/fn\s+/.test(line)) return "function";
      return "block";
    },
  },
};

export function parseCode(
  code: string,
  language: string,
  opts: { maxTokens: number }
): CodeChunk[] {
  if (!code || !code.trim()) return [];

  const patterns = PATTERNS[language] ?? PATTERNS["typescript"]!;
  const { maxTokens } = opts;
  const lines = code.split("\n");

  // Pass 1: separate import lines from the rest
  const importLines: { line: string; lineIdx: number }[] = [];
  const nonImportStartIdx = findNonImportStart(lines, patterns);

  for (let i = 0; i < nonImportStartIdx; i++) {
    importLines.push({ line: lines[i]!, lineIdx: i });
  }

  // Pass 2: collect boundary segments from the rest of the code
  interface Segment {
    lines: string[];
    startIdx: number; // 0-based line index in original file
    name: string;
    kind: "function" | "class" | "block" | "import";
  }

  const segments: Segment[] = [];
  let currentLines: string[] = [];
  let currentStart = nonImportStartIdx;
  let currentName = "";
  let currentKind: "function" | "class" | "block" | "import" = "block";

  function flushSegment() {
    if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
      segments.push({
        lines: currentLines,
        startIdx: currentStart,
        name: currentName,
        kind: currentKind,
      });
    }
  }

  for (let i = nonImportStartIdx; i < lines.length; i++) {
    const line = lines[i]!;
    if (patterns.boundary.test(line)) {
      flushSegment();
      currentLines = [line];
      currentStart = i;
      currentName = patterns.extractName(line);
      currentKind = patterns.classifyKind(line);
    } else {
      currentLines.push(line);
    }
  }
  flushSegment();

  const chunks: CodeChunk[] = [];

  // Emit import chunk
  if (importLines.length > 0) {
    const content = importLines.map((l) => l.line).join("\n");
    chunks.push({
      content,
      name: "imports",
      kind: "import",
      startLine: 1,
      endLine: importLines.length,
    });
  }

  // Emit code segments, splitting oversized ones at blank lines
  for (const seg of segments) {
    const segText = seg.lines.join("\n");
    if (estimateTokens(segText) <= maxTokens) {
      chunks.push({
        content: segText,
        name: seg.name,
        kind: seg.kind,
        startLine: seg.startIdx + 1,
        endLine: seg.startIdx + seg.lines.length,
      });
    } else {
      // Split at blank lines
      const subChunks = splitAtBlankLines(seg.lines, seg.startIdx, seg.name, seg.kind as "function" | "class" | "block", maxTokens);
      chunks.push(...subChunks);
    }
  }

  return chunks;
}

/** Find the index of the first non-import, non-blank line */
function findNonImportStart(lines: string[], patterns: LanguagePatterns): number {
  let lastImportOrBlank = 0;
  let seenImport = false;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    if (line.trim() === "") {
      // blank line — only counts as part of the import block if we've seen imports
      if (seenImport) lastImportOrBlank = i + 1;
      continue;
    }
    if (patterns.importLine.test(line)) {
      seenImport = true;
      lastImportOrBlank = i + 1;
    } else {
      break;
    }
  }

  return lastImportOrBlank;
}

function splitAtBlankLines(
  lines: string[],
  startIdx: number,
  name: string,
  kind: "function" | "class" | "block",
  maxTokens: number
): CodeChunk[] {
  const chunks: CodeChunk[] = [];
  let currentLines: string[] = [];
  let currentOffset = 0;

  function flush() {
    if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
      chunks.push({
        content: currentLines.join("\n"),
        name,
        kind,
        startLine: startIdx + currentOffset + 1,
        endLine: startIdx + currentOffset + currentLines.length,
      });
    }
    currentLines = [];
  }

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    currentLines.push(line);

    if (line.trim() === "") {
      const tokens = estimateTokens(currentLines.join("\n"));
      if (tokens >= maxTokens) {
        flush();
        currentOffset = i + 1;
      }
    }
  }
  flush();

  return chunks.length > 0 ? chunks : [{
    content: lines.join("\n"),
    name,
    kind,
    startLine: startIdx + 1,
    endLine: startIdx + lines.length,
  }];
}
