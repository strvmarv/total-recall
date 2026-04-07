import { describe, it, expect } from "vitest";
import { parseCode } from "./code-parser.js";

describe("parseCode", () => {
  it("splits TypeScript on function/class boundaries", () => {
    const code = `import { foo } from "foo";
import { bar } from "bar";

function greet(name: string): string {
  return "Hello " + name;
}

function farewell(name: string): string {
  return "Goodbye " + name;
}

class Greeter {
  private name: string;

  constructor(name: string) {
    this.name = name;
  }

  greet(): string {
    return "Hi " + this.name;
  }
}
`;
    const chunks = parseCode(code, "typescript", { maxTokens: 512 });

    // Should have 3+ chunks: imports, greet fn, farewell fn, Greeter class
    expect(chunks.length).toBeGreaterThanOrEqual(3);

    // Each chunk should have content
    for (const chunk of chunks) {
      expect(chunk.content.trim().length).toBeGreaterThan(0);
      expect(chunk.startLine).toBeGreaterThanOrEqual(1);
      expect(chunk.endLine).toBeGreaterThanOrEqual(chunk.startLine);
    }

    // Verify kinds include function and class
    const kinds = new Set(chunks.map((c) => c.kind));
    expect(kinds.has("function") || kinds.has("class")).toBe(true);
  });

  it("keeps imports as a separate chunk if present", () => {
    const importLines = Array.from(
      { length: 20 },
      (_, i) => `import { mod${i} } from "module-${i}";`
    ).join("\n");
    const code = `${importLines}

function doSomething(): void {
  console.log("doing something");
}
`;
    const chunks = parseCode(code, "typescript", { maxTokens: 512 });

    const importChunk = chunks.find((c) => c.kind === "import");
    expect(importChunk).toBeDefined();
    expect(importChunk!.content).toContain("import");
  });

  it("handles Python function and class boundaries", () => {
    const code = `import os
import sys

def greet(name: str) -> str:
    return f"Hello {name}"

def farewell(name: str) -> str:
    return f"Goodbye {name}"

class Greeter:
    def __init__(self, name: str):
        self.name = name

    def greet(self) -> str:
        return f"Hi {self.name}"
`;
    const chunks = parseCode(code, "python", { maxTokens: 512 });

    expect(chunks.length).toBeGreaterThanOrEqual(3);

    const kinds = new Set(chunks.map((c) => c.kind));
    expect(kinds.has("function") || kinds.has("class")).toBe(true);

    // Names should be extracted
    const names = chunks.map((c) => c.name).filter(Boolean);
    expect(names.length).toBeGreaterThan(0);
  });

  it("handles Go function boundaries", () => {
    const code = `package main

import "fmt"

func greet(name string) string {
\treturn "Hello " + name
}

func main() {
\tfmt.Println(greet("world"))
}
`;
    const chunks = parseCode(code, "go", { maxTokens: 512 });
    expect(chunks.length).toBeGreaterThanOrEqual(1);
    const kinds = new Set(chunks.map((c) => c.kind));
    expect(kinds.has("function")).toBe(true);
  });
});
