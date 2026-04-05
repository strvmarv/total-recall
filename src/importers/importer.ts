import type Database from "better-sqlite3";

export type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export interface ImportResult {
  imported: number;
  skipped: number;
  errors: string[];
}

export interface HostImporter {
  name: string;
  detect(): boolean;
  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number };
  importMemories(db: Database.Database, embed: EmbedFn, project?: string): Promise<ImportResult>;
  importKnowledge(db: Database.Database, embed: EmbedFn): Promise<ImportResult>;
}
