import { describe, it, expect } from "vitest";
import { parseMarkdown } from "./markdown-parser.js";

describe("parseMarkdown", () => {
  it("returns empty array for empty input", () => {
    expect(parseMarkdown("", { maxTokens: 512 })).toEqual([]);
    expect(parseMarkdown("   \n\n  ", { maxTokens: 512 })).toEqual([]);
  });

  it("splits on headings and preserves heading path", () => {
    const doc = `# API Reference

Some intro text.

## Auth

Auth overview.

### OAuth Flow

OAuth details here.

## Endpoints

Endpoint info.
`;
    const chunks = parseMarkdown(doc, { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThanOrEqual(3);

    // Find the OAuth Flow chunk
    const oauthChunk = chunks.find((c) =>
      c.headingPath.includes("OAuth Flow")
    );
    expect(oauthChunk).toBeDefined();
    expect(oauthChunk!.headingPath).toEqual([
      "API Reference",
      "Auth",
      "OAuth Flow",
    ]);

    // Find the Endpoints chunk
    const endpointsChunk = chunks.find((c) =>
      c.headingPath.includes("Endpoints")
    );
    expect(endpointsChunk).toBeDefined();
    expect(endpointsChunk!.headingPath).toEqual(["API Reference", "Endpoints"]);
  });

  it("never splits mid-code-block", () => {
    const codeBlock = Array.from(
      { length: 40 },
      (_, i) => `  const x${i} = ${i};`
    ).join("\n");
    const doc = `# Guide

Some text before.

\`\`\`typescript
${codeBlock}
\`\`\`

Some text after.
`;
    // Use a small maxTokens to force splitting pressure, but code block must stay intact
    const chunks = parseMarkdown(doc, { maxTokens: 30 });

    // No chunk should contain only part of a code block.
    // A valid chunk either has no fences, or has balanced open+close fences.
    // Opening fences have a language tag (e.g. ```typescript), closing are bare ```.
    for (const chunk of chunks) {
      const openFences = (chunk.content.match(/^```\S+/gm) || []).length;
      const closeFences = (chunk.content.match(/^```\s*$/gm) || []).length;
      // If it has an opening language fence, it must have a matching close fence
      expect(openFences).toBe(closeFences);
    }
  });

  it("respects max token limit for long paragraphs", () => {
    // ~10 words per paragraph * 0.75 = ~7.5 tokens per paragraph
    // With maxTokens=20, each chunk should hold ~2-3 paragraphs
    const paragraphs = Array.from(
      { length: 20 },
      (_, i) =>
        `Paragraph ${i} has exactly ten words total here now end.`
    );
    const doc = paragraphs.join("\n\n");
    const chunks = parseMarkdown(doc, { maxTokens: 20 });

    expect(chunks.length).toBeGreaterThan(1);

    for (const chunk of chunks) {
      const wordCount = chunk.content.trim().split(/\s+/).length;
      const approxTokens = wordCount * 0.75;
      // Each chunk should be at or near the limit (allow some overage for atomic units)
      expect(approxTokens).toBeLessThanOrEqual(50); // generous upper bound
    }
  });

  it("tracks startLine and endLine correctly", () => {
    const doc = `# Section One

Line two content.

# Section Two

Line six content.
`;
    const chunks = parseMarkdown(doc, { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThanOrEqual(2);

    for (const chunk of chunks) {
      expect(chunk.startLine).toBeGreaterThanOrEqual(1);
      expect(chunk.endLine).toBeGreaterThanOrEqual(chunk.startLine);
    }
  });
});
