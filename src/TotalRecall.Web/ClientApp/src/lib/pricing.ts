// Default per-model token prices in USD per 1,000,000 tokens. ESTIMATED — editable
// (persisted) pricing arrives with the Config section. Matched by substring against
// the raw model id (case-insensitive): claude model ids contain opus/sonnet/haiku.
export interface ModelRate { input: number; output: number; cacheWrite: number; cacheRead: number; }

const TABLE: { match: string; rate: ModelRate }[] = [
  { match: 'opus',   rate: { input: 15,  output: 75, cacheWrite: 18.75, cacheRead: 1.5 } },
  { match: 'sonnet', rate: { input: 3,   output: 15, cacheWrite: 3.75,  cacheRead: 0.3 } },
  { match: 'haiku',  rate: { input: 0.8, output: 4,  cacheWrite: 1.0,   cacheRead: 0.08 } },
];

export function rateForModel(model: string): ModelRate | null {
  const m = model.toLowerCase();
  for (const e of TABLE) if (m.includes(e.match)) return e.rate;
  return null;
}
