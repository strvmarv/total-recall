import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Dashboard } from './Dashboard';
import { api } from '../lib/api';

describe('Dashboard shell', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 't', backend: 'sqlite', version: 'x',
    };
    // Keep panels in loading state for the shell test (never resolves).
    vi.spyOn(api, 'tool').mockReturnValue(new Promise(() => {}));
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
    vi.restoreAllMocks();
  });

  it('renders the page heading and six panel regions', () => {
    render(<MemoryRouter><Dashboard /></MemoryRouter>);
    expect(screen.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
    // The Dashboard root and each Card render a <section aria-label> (role "region"):
    // 1 root + 4 cards + 2 peeks = 7. Counting regions keeps this shell test
    // independent of the panel titles, which the child components own.
    expect(screen.getAllByRole('region')).toHaveLength(7);
  });

  it('has a manual refresh control', () => {
    render(<MemoryRouter><Dashboard /></MemoryRouter>);
    expect(screen.getByRole('button', { name: /refresh/i })).toBeInTheDocument();
  });
});

describe('Dashboard integration', () => {
  const handlers: Record<string, unknown> = {
    status: { tierSizes: { hot_memories: 3, hot_knowledge: 0, warm_memories: 2466, warm_knowledge: 0, cold_memories: 22325, cold_knowledge: 0, pinned_memories: 12, pinned_knowledge: 0 }, knowledgeBase: { collections: [{ id: 'a', name: 'A' }], totalChunks: 100 }, db: { path: 'x', sizeBytes: 1, sessionId: 's' }, embedding: { model: 'b', dimensions: 1 } },
    usage_status: { query: { start_ms: 0, end_ms: Date.now(), group_by: 'day' }, buckets: [{ key: '2026-06-10', session_count: 1, turn_count: 1, input_tokens: 10, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 5 }], grand_total: { session_count: 1, turn_count: 1, input_tokens: 10, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 5 }, coverage: { sessions_with_full_token_data: 1, sessions_with_partial_token_data: 0, fidelity_percent: 100 } },
    eval_report: { precision: 0.8, hitRate: 0.75, missRate: 0.25, mrr: 0.6, avgLatencyMs: 42, totalEvents: 120 },
    memory_history: { movements: [{ id: '1', timestamp: Date.now(), session_id: null, source_tier: 'hot', target_tier: 'warm', source_entry_ids: [], target_entry_id: null, reason: 'decay' }], count: 1 },
    memory_list: { entries: [{ id: '1', tier: 'pinned', content_type: 'memory', content: 'pinned rule', summary: null, project: null, tags: [], created_at: 1, updated_at: 1, scope: 'u' }], count: 1, total: 12, limit: 8, offset: 0 },
    memory_recent: { entries: [{ id: '1', tier: 'warm', entry_type: 'decision', project: null, created_at: Date.now(), updated_at: 0, last_accessed_at: 0, preview: 'chose Recharts' }], count: 1, order: 'created' },
  };

  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' };
    vi.spyOn(api, 'tool').mockImplementation((name: string) => Promise.resolve(handlers[name]) as Promise<unknown>);
  });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders data across all four cards and both peeks', async () => {
    render(<MemoryRouter><Dashboard /></MemoryRouter>);
    expect(await screen.findByText('2466')).toBeInTheDocument();
    expect(await screen.findByText('15')).toBeInTheDocument();
    expect(await screen.findByText('75%')).toBeInTheDocument();
    expect(await screen.findByText(/1 movements/)).toBeInTheDocument();
    expect(await screen.findByText('pinned rule')).toBeInTheDocument();
    expect(await screen.findByText('chose Recharts')).toBeInTheDocument();
  });

  it('re-fetches when refresh is clicked', async () => {
    render(<MemoryRouter><Dashboard /></MemoryRouter>);
    await screen.findByText('2466');
    const callsBefore = (api.tool as ReturnType<typeof vi.fn>).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: /refresh/i }));
    await screen.findByText('2466');
    expect((api.tool as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(callsBefore);
  });
});
