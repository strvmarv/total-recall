import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Insights } from './Insights';
import { api } from '../lib/api';

describe('Insights page', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' };
    vi.spyOn(api, 'tool').mockImplementation((name: string) => {
      if (name === 'status') return Promise.resolve({ tierSizes: { hot_memories: 0, hot_knowledge: 0, warm_memories: 0, warm_knowledge: 0, cold_memories: 0, cold_knowledge: 0, pinned_memories: 20, pinned_knowledge: 0 }, knowledgeBase: { collections: [], totalChunks: 0 }, db: { path: 'x', sizeBytes: 1, sessionId: 's' }, embedding: { model: 'b', dimensions: 1 } }) as Promise<unknown>;
      if (name === 'eval_report') return Promise.resolve({ precision: 0.5, hitRate: 0.5, missRate: 0.5, mrr: 0.5, avgLatencyMs: 20, totalEvents: 10 }) as Promise<unknown>;
      if (name === 'usage_status') return Promise.resolve({ query: { start_ms: 0, end_ms: 1, group_by: 'day' }, buckets: [], grand_total: { session_count: 0, turn_count: 0, input_tokens: 0, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 }, coverage: { sessions_with_full_token_data: 0, sessions_with_partial_token_data: 0, fidelity_percent: 0 } }) as Promise<unknown>;
      return Promise.resolve({ entries: [], count: 0, order: 'created' }) as Promise<unknown>; // memory_recent
    });
  });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('shows the health score and suggestion cards', async () => {
    render(<MemoryRouter><Insights /></MemoryRouter>);
    expect(await screen.findByLabelText('Memory health score')).toBeInTheDocument();
    // pinned 20 (>15) + empty KB → at least these two suggestions
    expect(await screen.findByText('Many pinned directives')).toBeInTheDocument();
    expect(screen.getByText('Knowledge base is empty')).toBeInTheDocument();
  });
});
