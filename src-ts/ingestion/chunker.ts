import { parseMarkdown } from "./markdown-parser.js";
import { parseCode } from "./code-parser.js";

export interface Chunk {
  content: string;
  headingPath?: string[];
  name?: string;
  kind?: string;
  startLine: number;
  endLine: number;
}

const MARKDOWN_EXTENSIONS = new Set([".md", ".mdx", ".markdown"]);

const CODE_LANGUAGE_MAP: Record<string, string> = {
  ".ts": "typescript",
  ".tsx": "typescript",
  ".js": "javascript",
  ".jsx": "javascript",
  ".py": "python",
  ".go": "go",
  ".rs": "rust",
};

function getExtension(filePath: string): string {
  const base = filePath.split("/").pop() ?? filePath;
  const dotIdx = base.lastIndexOf(".");
  if (dotIdx === -1) return "";
  return base.slice(dotIdx).toLowerCase();
}

function estimateTokens(text: string): number {
  const wordCount = text.trim().split(/\s+/).filter(Boolean).length;
  return Math.ceil(wordCount * 0.75);
}

function splitByParagraphs(
  content: string,
  maxTokens: number
): Chunk[] {
  const paragraphs = content.split(/\n\n+/);
  const chunks: Chunk[] = [];

  let currentParts: string[] = [];
  let lineOffset = 1;
  let currentStartLine = 1;

  // Track line numbers per paragraph
  let lineCount = 1;

  for (const para of paragraphs) {
    const paraLines = para.split("\n").length;
    const paraTokens = estimateTokens(para);
    const currentTokens = estimateTokens(currentParts.join("\n\n"));

    if (currentParts.length === 0) {
      currentParts.push(para);
      currentStartLine = lineCount;
    } else if (currentTokens + paraTokens <= maxTokens) {
      currentParts.push(para);
    } else {
      // Flush
      const content = currentParts.join("\n\n");
      const contentLines = content.split("\n").length;
      chunks.push({
        content,
        startLine: currentStartLine,
        endLine: currentStartLine + contentLines - 1,
      });
      currentParts = [para];
      currentStartLine = lineCount;
    }

    lineCount += paraLines + 1; // +1 for the blank line separator
    lineOffset = lineCount;
  }

  if (currentParts.length > 0) {
    const content = currentParts.join("\n\n");
    const contentLines = content.split("\n").length;
    chunks.push({
      content,
      startLine: currentStartLine,
      endLine: currentStartLine + contentLines - 1,
    });
  }

  return chunks;
}

export function chunkFile(
  content: string,
  filePath: string,
  opts: { maxTokens: number; overlapTokens?: number }
): Chunk[] {
  if (!content || !content.trim()) return [];

  const ext = getExtension(filePath);

  if (MARKDOWN_EXTENSIONS.has(ext)) {
    const mdChunks = parseMarkdown(content, opts);
    return mdChunks.map((c) => ({
      content: c.content,
      headingPath: c.headingPath,
      startLine: c.startLine,
      endLine: c.endLine,
    }));
  }

  const language = CODE_LANGUAGE_MAP[ext];
  if (language !== undefined) {
    const codeChunks = parseCode(content, language, opts);
    return codeChunks.map((c) => ({
      content: c.content,
      name: c.name,
      kind: c.kind,
      startLine: c.startLine,
      endLine: c.endLine,
    }));
  }

  // Fallback: paragraph-based splitting
  return splitByParagraphs(content, opts.maxTokens);
}
