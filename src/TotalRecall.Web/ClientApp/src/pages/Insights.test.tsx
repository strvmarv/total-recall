import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { Insights } from './Insights';
import { api } from '../lib/api';
import type { InsightsResult, UsageResult } from '../lib/types';

const USAGE: UsageResult = {
  query: { start_ms: 0, end_ms: 1, group_by: 'day' },
  buckets: [],
  grand_total: { session_count: 0, turn_count: 0, input_tokens: 0, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 },
  coverage: { sessions_with_full_token_data: 0, sessions_with_partial_token_data: 0, fidelity_percent: 0 },
};

function insights(over: Partial<InsightsResult> = {}): InsightsResult {
  return {
    healthScore: 72,
    healthBreakdown: {
      retrieval: { score: 30, max: 35, detail: 'hit rate 86%' },
      capture: { score: 18, max: 25, detail: '4 of 10 recent entries are curated' },
      pinned: { score: 14, max: 20, detail: '3 pinned — within budget' },
      kb: { score: 10, max: 20, detail: 'no knowledge base ingested' },
    },
    nearDuplicates: [],
    pinCandidates: [],
    retrievalGaps: [],
    thresholdCurve: { current: 0.7, points: [
      { threshold: 0.6, hitRate: 0.6, precision: 0.6, mrr: 0.55 },
      { threshold: 0.7, hitRate: 0.7, precision: 0.7, mrr: 0.60 },
      { threshold: 0.8, hitRate: 0.6, precision: 0.8, mrr: 0.70 }, // best mrr → suggest 0.8
    ] },
    ...over,
  };
}

/** Mock api.tool: insights returns the supplied payload, usage_status the fixture,
 *  everything else resolves so action calls succeed. `calls` records every call. */
function mockApi(payload: InsightsResult, calls: { name: string; args?: unknown }[], overrides?: (name: string, args?: unknown) => Promise<unknown> | undefined) {
  vi.spyOn(api, 'tool').mockImplementation((name: string, args?: unknown) => {
    calls.push({ name, args });
    const o = overrides?.(name, args);
    if (o) return o as Promise<never>;
    if (name === 'insights') return Promise.resolve(payload) as Promise<never>;
    if (name === 'usage_status') return Promise.resolve(USAGE) as Promise<never>;
    return Promise.resolve({}) as Promise<never>; // memory_pin / memory_delete / config_set
  });
}

function renderPage() {
  return render(<MemoryRouter><Insights /></MemoryRouter>);
}

describe('Insights page', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' };
  });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders the health score and fetches insights + usage', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights(), calls);
    renderPage();
    expect(await screen.findByLabelText('Memory health score')).toBeInTheDocument();
    expect(screen.getByText('72')).toBeInTheDocument();
    await waitFor(() => {
      expect(calls.find((c) => c.name === 'insights')).toBeTruthy();
      expect(calls.find((c) => c.name === 'usage_status')?.args).toEqual({ window: '30d', group_by: 'day' });
    });
  });

  it('shows an inline alert when the insights fetch fails', async () => {
    vi.spyOn(api, 'tool').mockImplementation((name: string) => {
      if (name === 'insights') return Promise.reject(new Error('boom')) as Promise<never>;
      return Promise.resolve(USAGE) as Promise<never>; // usage_status (tolerated)
    });
    renderPage();
    const alert = await screen.findByText("Couldn't compute insights.");
    expect(alert).toHaveAttribute('role', 'alert');
  });

  it('expands the health breakdown disclosure on click', async () => {
    mockApi(insights(), []);
    renderPage();
    const toggle = await screen.findByRole('button', { name: /show breakdown/i });
    expect(screen.queryByText('Retrieval')).not.toBeInTheDocument();
    await userEvent.click(toggle);
    // all four health components render
    expect(screen.getByText('Retrieval')).toBeInTheDocument();
    expect(screen.getByText('Capture mix')).toBeInTheDocument();
    expect(screen.getByText('Pinned discipline')).toBeInTheDocument();
    expect(screen.getByText('Knowledge base')).toBeInTheDocument();
    expect(screen.getByText('hit rate 86%')).toBeInTheDocument();
  });

  it('pin card: clicking Pin calls memory_pin with the candidate id and refetches', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights({ pinCandidates: [{ id: 'p1', tier: 'warm', preview: 'pin me', accessCount: 12 }] }), calls);
    renderPage();
    const pinBtn = await screen.findByRole('button', { name: /^pin$/i });
    await userEvent.click(pinBtn);
    await waitFor(() => expect(calls.find((c) => c.name === 'memory_pin')?.args).toEqual({ id: 'p1' }));
    // refetch: insights queried again after the action (initial + refresh)
    await waitFor(() => expect(calls.filter((c) => c.name === 'insights').length).toBeGreaterThanOrEqual(2));
  });

  it('near-dup card: two-click confirm deletes each older member then refetches', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights({ nearDuplicates: [{
      groupId: 'g', topScore: 0.95,
      members: [
        { id: 'old1', tier: 'warm', preview: 'dup', score: 0.95, createdAt: 1000 },
        { id: 'newest', tier: 'hot', preview: 'dup newest', score: 0.95, createdAt: 3000 },
        { id: 'old2', tier: 'warm', preview: 'dup', score: 0.9, createdAt: 2000 },
      ],
    }] }), calls);
    renderPage();
    // first click → confirm state
    const del = await screen.findByRole('button', { name: /keep newest, delete the rest/i });
    await userEvent.click(del);
    const confirm = await screen.findByRole('button', { name: /confirm: delete 2/i });
    // second click → deletes each older member
    await userEvent.click(confirm);
    await waitFor(() => {
      const deletes = calls.filter((c) => c.name === 'memory_delete');
      expect(deletes.map((c) => (c.args as { id: string }).id).sort()).toEqual(['old1', 'old2']);
    });
    // refetch happened
    await waitFor(() => expect(calls.filter((c) => c.name === 'insights').length).toBeGreaterThanOrEqual(2));
  });

  it('near-dup card: first click only arms the confirm — no delete yet', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights({ nearDuplicates: [{
      groupId: 'g', topScore: 0.95,
      members: [
        { id: 'old1', tier: 'warm', preview: 'dup', score: 0.95, createdAt: 1000 },
        { id: 'newest', tier: 'hot', preview: 'dup newest', score: 0.95, createdAt: 3000 },
      ],
    }] }), calls);
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: /keep newest, delete the rest/i }));
    expect(await screen.findByRole('button', { name: /confirm: delete 1/i })).toBeInTheDocument();
    expect(calls.find((c) => c.name === 'memory_delete')).toBeUndefined();
  });

  it('threshold card: Apply calls config_set with the warm similarity key and suggested value', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights(), calls); // current 0.7, suggested 0.8
    renderPage();
    const apply = await screen.findByRole('button', { name: /^apply$/i });
    await userEvent.click(apply);
    await waitFor(() => expect(calls.find((c) => c.name === 'config_set')?.args)
      .toEqual({ key: 'tiers.warm.similarity_threshold', value: 0.8 }));
    await waitFor(() => expect(calls.filter((c) => c.name === 'insights').length).toBeGreaterThanOrEqual(2));
  });

  it('surfaces an inline alert when a pin action fails', async () => {
    mockApi(insights({ pinCandidates: [{ id: 'p1', tier: 'warm', preview: 'pin me', accessCount: 12 }] }), [],
      (name) => name === 'memory_pin' ? Promise.reject(new Error('pin denied')) : undefined);
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: /^pin$/i }));
    const alert = await screen.findByText('pin denied');
    expect(alert).toHaveAttribute('role', 'alert');
  });

  it('surfaces an inline alert when a near-dup delete fails (and still refetches)', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights({ nearDuplicates: [{
      groupId: 'g', topScore: 0.95,
      members: [
        { id: 'old1', tier: 'warm', preview: 'dup', score: 0.95, createdAt: 1000 },
        { id: 'newest', tier: 'hot', preview: 'dup newest', score: 0.95, createdAt: 3000 },
      ],
    }] }), calls,
      (name) => name === 'memory_delete' ? Promise.reject(new Error('delete denied')) : undefined);
    renderPage();
    // two-click confirm
    await userEvent.click(await screen.findByRole('button', { name: /keep newest, delete the rest/i }));
    await userEvent.click(await screen.findByRole('button', { name: /confirm: delete 1/i }));
    // inline alert surfaces (Promise.allSettled → thrown "N delete(s) failed")
    const alert = await screen.findByText(/1 delete\(s\) failed/i);
    expect(alert).toHaveAttribute('role', 'alert');
    // refetch ran despite the failure
    await waitFor(() => expect(calls.filter((c) => c.name === 'insights').length).toBeGreaterThanOrEqual(2));
  });

  it('gap card: renders the query and an "Open in Eval" link to /eval', async () => {
    mockApi(insights({ retrievalGaps: [{ query: 'how to deploy', timesSeen: 7, topScore: 0.41 }] }), []);
    renderPage();
    expect(await screen.findByText('how to deploy')).toBeInTheDocument();
    const link = await screen.findByRole('link', { name: /open in eval/i });
    expect(link).toHaveAttribute('href', '/eval');
  });

  it('cost-spike card: renders when week-over-week tokens jump and links to /usage', async () => {
    // end_ms anchored at a fixed UTC midnight; current week tokens >> prior week → spike.
    const endMs = Date.parse('2026-06-15T00:00:00Z');
    const bucket = (key: string, tokens: number): UsageResult['buckets'][number] => ({
      key, session_count: 1, turn_count: 1,
      input_tokens: tokens, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0,
    });
    const spikeUsage: UsageResult = {
      ...USAGE,
      query: { start_ms: endMs - 14 * 86_400_000, end_ms: endMs, group_by: 'day' },
      buckets: [
        bucket('2026-06-04', 100), // prior week
        bucket('2026-06-11', 500), // current week → +400%
      ],
    };
    const calls: { name: string; args?: unknown }[] = [];
    mockApi(insights(), calls,
      (name) => name === 'usage_status' ? Promise.resolve(spikeUsage) as Promise<never> : undefined);
    renderPage();
    expect(await screen.findByText(/token usage is rising/i)).toBeInTheDocument();
    const link = await screen.findByRole('link', { name: /open usage/i });
    expect(link).toHaveAttribute('href', '/usage');
  });

  it('shows the all-clear empty state when there are no cards', async () => {
    // clean payload: no findings, suggested threshold == current → no threshold card
    mockApi(insights({ thresholdCurve: { current: 0.7, points: [
      { threshold: 0.7, hitRate: 0.7, precision: 0.7, mrr: 0.9 },
    ] } }), []);
    renderPage();
    expect(await screen.findByText(/all clear/i)).toBeInTheDocument();
  });

  it('renders the no-LLM footnote', async () => {
    mockApi(insights(), []);
    renderPage();
    expect(await screen.findByText(/computed server-side from your local store/i)).toBeInTheDocument();
    expect(screen.getByText(/no llm/i)).toBeInTheDocument();
  });
});
