import { describe, it, expect } from "vitest";
import { chunkFile } from "./chunker.js";

describe("chunkFile", () => {
  it("detects markdown and uses markdown parser", () => {
    const content = `# Introduction

This is the intro section.

## Details

These are the details.
`;
    const chunks = chunkFile(content, "README.md", { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThan(0);
    // Markdown chunks should have headingPath
    const withHeadings = chunks.filter((c) => c.headingPath && c.headingPath.length > 0);
    expect(withHeadings.length).toBeGreaterThan(0);
  });

  it("detects TypeScript and uses code parser", () => {
    const content = `import { foo } from "foo";

function hello(name: string): string {
  return "Hello " + name;
}

class Greeter {
  greet() { return "hi"; }
}
`;
    const chunks = chunkFile(content, "src/hello.ts", { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThan(0);
    for (const chunk of chunks) {
      expect(chunk.content.trim().length).toBeGreaterThan(0);
      expect(chunk.startLine).toBeGreaterThanOrEqual(1);
    }
  });

  it("detects language from .py file extension", () => {
    const content = `import os

def greet(name: str) -> str:
    return f"Hello {name}"

class Greeter:
    def say_hi(self):
        return "hi"
`;
    const chunks = chunkFile(content, "/path/to/script.py", { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThan(0);
    const withKind = chunks.filter((c) => c.kind);
    expect(withKind.length).toBeGreaterThan(0);
  });

  it("falls back to paragraph splitting for unknown formats", () => {
    const paragraphs = Array.from(
      { length: 10 },
      (_, i) => `This is paragraph ${i} with some text in it to make it longer.`
    ).join("\n\n");
    const chunks = chunkFile(paragraphs, "notes.txt", { maxTokens: 20 });
    expect(chunks.length).toBeGreaterThan(1);
    for (const chunk of chunks) {
      expect(chunk.content.trim().length).toBeGreaterThan(0);
    }
  });

  it("handles .mdx extension as markdown", () => {
    const content = `# MDX Page

Some content here.
`;
    const chunks = chunkFile(content, "page.mdx", { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThan(0);
    const withHeadings = chunks.filter((c) => c.headingPath);
    expect(withHeadings.length).toBeGreaterThan(0);
  });

  it("handles .tsx extension as typescript", () => {
    const content = `import React from "react";

function MyComponent() {
  return <div>Hello</div>;
}
`;
    const chunks = chunkFile(content, "MyComponent.tsx", { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThan(0);
  });
});
