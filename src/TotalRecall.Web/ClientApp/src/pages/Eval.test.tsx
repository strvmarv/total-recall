import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { Eval } from './Eval';
import { api } from '../lib/api';
import type {
  EvalReport, EvalBenchmarkResult, EvalCompareResult,
  EvalGrowListResult, EvalGrowResolveResult, EvalSnapshotResult,
} from '../lib/types';

const REPORT: EvalReport = {
  precision: 0.82, hitRate: 0.75, missRate: 0.25, mrr: 0.6, avgLatencyMs: 42, totalEvents: 120,
  byTier: { hot: { precision: 0.9, hitRate: 0.8, avgScore: 0.71, count: 60 } },
  byContentType: { fact: { precision: 0.85, hitRate: 0.78, count: 80 } },
  topMisses: [{ query: 'how to reset widget', topScore: 0.41, timestamp: 1_700_000_000_000 }],
  falsePositives: [{ query: 'stale doc', topScore: 0.66, timestamp: 1_700_000_000_000 }],
  compactionHealth: { totalCompactions: 5, avgPreservationRatio: 0.92, entriesWithDrift: 1 },
};

const BENCHMARK: EvalBenchmarkResult = {
  totalQueries: 12, exactMatchRate: 0.83, fuzzyMatchRate: 0.92, tierRoutingRate: 0.75,
  negativePassRate: 1, avgLatencyMs: 18,
  details: [
    { query: 'capital of france', expectedContains: 'Paris', topResult: 'Paris is the capital', topScore: 0.88, matched: true, fuzzyMatched: true, hasNegativeAssertion: false, negativePass: true },
  ],
};

const GROW_LIST: EvalGrowListResult = {
  action: 'list',
  candidates: [
    { id: 'c1', queryText: 'how to deploy', topScore: 0.4, topResultContent: 'deploy doc', topResultEntryId: 'e1', firstSeen: 1, lastSeen: 2, timesSeen: 7, status: 'pending' },
    { id: 'c2', queryText: 'rollback steps', topScore: 0.3, topResultContent: null, topResultEntryId: null, firstSeen: 1, lastSeen: 2, timesSeen: 3, status: 'pending' },
  ],
  count: 2,
};

const GROW_RESOLVE: EvalGrowResolveResult = {
  action: 'resolve', accepted: 1, rejected: 1, corpusEntries: ['c1'], benchmarkPath: '/data/benchmark.jsonl',
};

const COMPARE: EvalCompareResult = {
  beforeId: 'snap-a', afterId: 'latest',
  deltas: { precision: 0.05, hitRate: 0.03, mrr: 0.02, missRate: -0.03, avgLatencyMs: -4 },
  regressions: [{ queryText: 'q regressed', beforeOutcome: 'hit', afterOutcome: 'miss', beforeScore: 0.8, afterScore: 0.3 }],
  improvements: [{ queryText: 'q improved', beforeOutcome: 'miss', afterOutcome: 'hit', beforeScore: 0.2, afterScore: 0.9 }],
  warning: null,
};

const SNAPSHOT: EvalSnapshotResult = { id: 'snap-xyz', name: 'baseline', deduped: false };

/** Default mock: report + grow-list resolve immediately; other tools pend unless overridden. */
function mockApi(overrides?: (name: string, args?: unknown) => Promise<unknown> | undefined) {
  vi.spyOn(api, 'tool').mockImplementation((name: string, args?: unknown) => {
    const o = overrides?.(name, args);
    if (o) return o as Promise<never>;
    if (name === 'eval_report') return Promise.resolve(REPORT) as Promise<never>;
    if (name === 'eval_grow') return Promise.resolve(GROW_LIST) as Promise<never>;
    return new Promise<never>(() => {}); // benchmark/compare/snapshot only on demand
  });
}

function renderEval(initialEntries: string[] = ['/eval']) {
  return render(<MemoryRouter initialEntries={initialEntries}><Eval /></MemoryRouter>);
}

describe('Eval page', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' };
  });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders the heading and all four section cards', async () => {
    mockApi();
    renderEval();
    expect(screen.getByRole('heading', { name: 'Eval', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /retrieval report/i })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /run benchmark/i })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /grow benchmark/i })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /compare snapshots/i })).toBeInTheDocument();
  });

  it('renders report metrics, per-tier and per-content-type tables, and top misses', async () => {
    mockApi();
    renderEval();
    // headline metric
    expect(await screen.findByText('82%')).toBeInTheDocument(); // precision
    // per-tier table row
    expect(await screen.findByText('hot')).toBeInTheDocument();
    // per-content-type row
    expect(await screen.findByText('fact')).toBeInTheDocument();
    // top miss
    expect(await screen.findByText('how to reset widget')).toBeInTheDocument();
  });

  it('re-queries the report when the window selector changes to 30 days', async () => {
    const spy = vi.fn();
    mockApi((name, args) => { if (name === 'eval_report') { spy(args); return Promise.resolve(REPORT); } return undefined; });
    renderEval();
    await screen.findByText('82%');
    await waitFor(() => expect(spy).toHaveBeenCalledWith(expect.objectContaining({ days: 7 })));
    await userEvent.selectOptions(screen.getByLabelText(/report window/i), '30');
    await waitFor(() => expect(spy).toHaveBeenCalledWith(expect.objectContaining({ days: 30 })));
  });

  it('runs the benchmark on click and renders the rates + details table', async () => {
    let resolveBench: (v: EvalBenchmarkResult) => void = () => {};
    mockApi((name) => {
      if (name === 'eval_benchmark') return new Promise<EvalBenchmarkResult>((res) => { resolveBench = res; });
      return undefined;
    });
    const spy = vi.spyOn(api, 'tool');
    renderEval();
    const btn = await screen.findByRole('button', { name: /run benchmark/i });
    await userEvent.click(btn);
    expect(spy).toHaveBeenCalledWith('eval_benchmark', undefined);
    expect(btn).toBeDisabled(); // in-flight loading state
    resolveBench(BENCHMARK);
    expect(await screen.findByText('83%')).toBeInTheDocument(); // exact match rate
    expect(screen.getByText('capital of france')).toBeInTheDocument(); // details row
    await waitFor(() => expect(btn).not.toBeDisabled());
  });

  it('grow: selecting accept/reject then Resolve calls eval_grow with the right args and refreshes', async () => {
    const calls: { name: string; args?: unknown }[] = [];
    vi.spyOn(api, 'tool').mockImplementation((name: string, args?: unknown) => {
      calls.push({ name, args });
      if (name === 'eval_report') return Promise.resolve(REPORT) as Promise<never>;
      if (name === 'eval_grow') {
        const a = args as { action: string } | undefined;
        if (a?.action === 'resolve') return Promise.resolve(GROW_RESOLVE) as Promise<never>;
        return Promise.resolve(GROW_LIST) as Promise<never>;
      }
      return new Promise<never>(() => {}) as Promise<never>;
    });
    renderEval();
    // candidate rows load
    const row1 = (await screen.findByText('how to deploy')).closest('tr')!;
    const row2 = screen.getByText('rollback steps').closest('tr')!;
    // accept c1, reject c2
    await userEvent.click(within(row1).getByRole('button', { name: /accept/i }));
    await userEvent.click(within(row2).getByRole('button', { name: /reject/i }));
    await userEvent.click(screen.getByRole('button', { name: /^resolve/i }));
    await waitFor(() => {
      const resolve = calls.find((c) => c.name === 'eval_grow' && (c.args as { action?: string })?.action === 'resolve');
      expect(resolve).toBeTruthy();
      expect(resolve!.args).toEqual({ action: 'resolve', accept: ['c1'], reject: ['c2'] });
    });
    // resolve result surfaced
    expect(await screen.findByText(/\/data\/benchmark\.jsonl/)).toBeInTheDocument();
  });

  it('compare: Compare button calls eval_compare and renders deltas + regressions/improvements', async () => {
    mockApi((name) => { if (name === 'eval_compare') return Promise.resolve(COMPARE); return undefined; });
    const spy = vi.spyOn(api, 'tool');
    renderEval();
    // before is required, so the Compare button stays disabled until an id is entered
    const compareBtn = await screen.findByRole('button', { name: /^compare/i });
    expect(compareBtn).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/before/i), 'snap-a');
    expect(compareBtn).not.toBeDisabled();
    await userEvent.click(compareBtn);
    await waitFor(() => expect(spy).toHaveBeenCalledWith('eval_compare', expect.objectContaining({ before: 'snap-a', after: 'latest' })));
    expect(await screen.findByText('q regressed')).toBeInTheDocument();
    expect(screen.getByText('q improved')).toBeInTheDocument();
  });

  it('snapshot: name + button calls eval_snapshot and shows the returned id', async () => {
    mockApi((name) => { if (name === 'eval_snapshot') return Promise.resolve(SNAPSHOT); return undefined; });
    const spy = vi.spyOn(api, 'tool');
    renderEval();
    await userEvent.type(await screen.findByLabelText(/snapshot name/i), 'baseline');
    await userEvent.click(screen.getByRole('button', { name: /^snapshot/i }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith('eval_snapshot', { name: 'baseline' }));
    expect(await screen.findByText(/snap-xyz/)).toBeInTheDocument();
  });

  // ── error paths: each async action surfaces its section's alert when the tool rejects ──

  it('benchmark: surfaces the failure alert when eval_benchmark rejects', async () => {
    mockApi((name) => { if (name === 'eval_benchmark') return Promise.reject(new Error('embedder unavailable')); return undefined; });
    renderEval();
    await userEvent.click(await screen.findByRole('button', { name: /run benchmark/i }));
    const alert = await screen.findByText('Benchmark failed.');
    expect(alert).toHaveAttribute('role', 'alert');
    expect(alert).toHaveAttribute('title', 'embedder unavailable');
  });

  it('grow: surfaces the resolve failure alert when eval_grow resolve rejects', async () => {
    mockApi((name, args) => {
      if (name === 'eval_grow' && (args as { action?: string })?.action === 'resolve') {
        return Promise.reject(new Error('write denied'));
      }
      return undefined;
    });
    renderEval();
    const row1 = (await screen.findByText('how to deploy')).closest('tr')!;
    await userEvent.click(within(row1).getByRole('button', { name: /accept/i }));
    await userEvent.click(screen.getByRole('button', { name: /^resolve/i }));
    const alert = await screen.findByText('Resolve failed.');
    expect(alert).toHaveAttribute('role', 'alert');
    expect(alert).toHaveAttribute('title', 'write denied');
  });

  it('compare: surfaces the failure alert when eval_compare rejects', async () => {
    mockApi((name) => { if (name === 'eval_compare') return Promise.reject(new Error('baseline is required')); return undefined; });
    renderEval();
    // before is required, so enter an id to enable the Compare button
    await userEvent.type(await screen.findByLabelText(/before/i), 'snap-a');
    await userEvent.click(screen.getByRole('button', { name: /^compare/i }));
    const alert = await screen.findByText('Compare failed.');
    expect(alert).toHaveAttribute('role', 'alert');
    expect(alert).toHaveAttribute('title', 'baseline is required');
  });

  it('snapshot: surfaces the failure alert when eval_snapshot rejects', async () => {
    mockApi((name) => { if (name === 'eval_snapshot') return Promise.reject(new Error('disk full')); return undefined; });
    renderEval();
    await userEvent.type(await screen.findByLabelText(/snapshot name/i), 'baseline');
    await userEvent.click(screen.getByRole('button', { name: /^snapshot/i }));
    const alert = await screen.findByText('Snapshot failed.');
    expect(alert).toHaveAttribute('role', 'alert');
    expect(alert).toHaveAttribute('title', 'disk full');
  });

  it('benchmark: shows OperationProgress with elapsed time while eval_benchmark is in-flight', async () => {
    let resolveBench: (v: EvalBenchmarkResult) => void = () => {};
    mockApi((name) => {
      if (name === 'eval_benchmark') return new Promise<EvalBenchmarkResult>((res) => { resolveBench = res; });
      return undefined;
    });
    renderEval();
    // Wait for the component to finish its initial data load under real timers,
    // so findByRole polling doesn't fight fake timers.
    const btn = await screen.findByRole('button', { name: /run benchmark/i });
    // Switch to fake timers now — before the click — so the component's setInterval
    // (created inside the useEffect that watches busy) runs under fake timers.
    vi.useFakeTimers();
    try {
      // Use fireEvent (synchronous) to avoid userEvent's internal setTimeout delays
      // that would hang under fake timers.
      act(() => { fireEvent.click(btn); });
      // Advance 3 s under fake timers so the setInterval fires and elapsedMs reaches 3000.
      act(() => { vi.advanceTimersByTime(3000); });
      // Elapsed must actually read "3s", not just the initial "0s".
      expect(screen.getByText(/Running benchmark… 3s/)).toBeInTheDocument();
      resolveBench(BENCHMARK);
    } finally {
      vi.useRealTimers();
    }
  });

  it('highlights the matching grow candidate from the query param', async () => {
    mockApi();
    renderEval(['/eval?grow=how%20to%20deploy']);
    const row = await screen.findByText('how to deploy');
    expect(row.closest('tr')).toHaveClass('tr-row-hl');
    expect(screen.getByText(/looking for/i)).toHaveTextContent('how to deploy');
  });

  it('shows the banner with a reason when there is no matching candidate', async () => {
    mockApi();
    renderEval(['/eval?grow=missing%20query']);
    expect(await screen.findByText(/looking for/i)).toHaveTextContent('missing query');
    expect(screen.getByText(/not a current candidate/i)).toBeInTheDocument();
  });
});
