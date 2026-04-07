export interface MarkdownChunk {
  content: string;
  headingPath: string[];
  startLine: number;
  endLine: number;
}

function estimateTokens(text: string): number {
  const wordCount = text.trim().split(/\s+/).filter(Boolean).length;
  return Math.ceil(wordCount * 0.75);
}

interface Section {
  headingPath: string[];
  lines: string[];
  startLine: number;
}

export function parseMarkdown(
  text: string,
  opts: { maxTokens: number; overlapTokens?: number }
): MarkdownChunk[] {
  if (!text || !text.trim()) return [];

  const { maxTokens } = opts;
  const allLines = text.split("\n");

  const sections: Section[] = [];
  let currentHeadingPath: string[] = [];
  let currentLines: string[] = [];
  let currentStartLine = 1;

  const headingRe = /^(#{1,6})\s+(.+)$/;

  function flushSection() {
    if (currentLines.length > 0) {
      sections.push({
        headingPath: [...currentHeadingPath],
        lines: currentLines,
        startLine: currentStartLine,
      });
    }
  }

  for (let i = 0; i < allLines.length; i++) {
    const line = allLines[i]!;
    const match = headingRe.exec(line);
    if (match) {
      flushSection();

      const level = match[1]!.length;
      const title = match[2]!.trim();

      currentHeadingPath = currentHeadingPath.slice(0, level - 1);
      currentHeadingPath[level - 1] = title;

      currentLines = [line];
      currentStartLine = i + 1;
    } else {
      currentLines.push(line);
    }
  }
  flushSection();

  const chunks: MarkdownChunk[] = [];

  for (const section of sections) {
    const sectionText = section.lines.join("\n");
    if (estimateTokens(sectionText) <= maxTokens) {
      chunks.push({
        content: sectionText,
        headingPath: section.headingPath,
        startLine: section.startLine,
        endLine: section.startLine + section.lines.length - 1,
      });
    } else {
      const subChunks = splitSection(section, maxTokens);
      chunks.push(...subChunks);
    }
  }

  return chunks;
}

interface AtomicBlock {
  lines: string[];
  lineOffset: number;
}

function splitSection(section: Section, maxTokens: number): MarkdownChunk[] {
  const { headingPath, lines, startLine } = section;

  const blocks: AtomicBlock[] = [];
  let i = 0;
  const codeFenceRe = /^```/;

  while (i < lines.length) {
    const line = lines[i]!;
    if (codeFenceRe.test(line)) {
      const blockLines: string[] = [line];
      const offset = i;
      i++;
      while (i < lines.length) {
        const inner = lines[i]!;
        blockLines.push(inner);
        i++;
        if (/^```\s*$/.test(inner)) break;
      }
      blocks.push({ lines: blockLines, lineOffset: offset });
    } else {
      const blockLines: string[] = [];
      const offset = i;
      while (i < lines.length && !/^```/.test(lines[i]!)) {
        blockLines.push(lines[i]!);
        i++;
        if (blockLines[blockLines.length - 1]!.trim() === "") break;
      }
      if (blockLines.length > 0) {
        blocks.push({ lines: blockLines, lineOffset: offset });
      }
    }
  }

  const chunks: MarkdownChunk[] = [];
  let currentBlockLines: string[] = [];
  let currentOffset = 0;

  function flushChunk() {
    if (currentBlockLines.length === 0) return;
    const content = currentBlockLines.join("\n");
    chunks.push({
      content,
      headingPath,
      startLine: startLine + currentOffset,
      endLine: startLine + currentOffset + currentBlockLines.length - 1,
    });
    currentBlockLines = [];
  }

  for (const block of blocks) {
    const blockText = block.lines.join("\n");
    const blockTokens = estimateTokens(blockText);
    const currentTokens = estimateTokens(currentBlockLines.join("\n"));

    if (currentBlockLines.length === 0) {
      currentBlockLines = [...block.lines];
      currentOffset = block.lineOffset;
    } else if (currentTokens + blockTokens <= maxTokens) {
      currentBlockLines.push(...block.lines);
    } else {
      flushChunk();
      currentBlockLines = [...block.lines];
      currentOffset = block.lineOffset;
    }
  }
  flushChunk();

  return chunks;
}
