import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Config } from './Config';
import { api } from '../lib/api';

const CONFIG = {
  config: {
    tiers: { hot: { max_entries: 50, token_budget: 4000, carry_forward_threshold: 0.7 }, warm: { max_entries: 10000, retrieval_top_k: 5, similarity_threshold: 0.65, cold_decay_days: 30 }, cold: { chunk_max_tokens: 512, chunk_overlap_tokens: 50, lazy_summary_threshold: 5 }, pinned: { max_content_chars: 500, floor_enabled: true, floor_every_n_turns: 6, floor_growth_tokens: 6000 } },
    compaction: { decay_half_life_hours: 168, warm_threshold: 0.3, promote_threshold: 0.7, warm_sweep_interval_days: 7, auto_demote_min_injections: 10 },
    search: { fts_weight: 0.3 },
    regression: { miss_rate_delta: 0.1, latency_ratio: 2, min_events: 10 },
    tool_cache: { max_entries: 200, default_ttl_seconds: 600 },
    scope: { default: 'user:local' },
    embedding: { model: 'bge-small-en-v1.5', dimensions: 384 },
    storage: { mode: 'sqlite' },
  },
};

describe('Config page', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders editable fields seeded from config_get and a read-only embedding value', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(CONFIG);
    render(<Config />);
    expect(await screen.findByLabelText(/similarity threshold/i)).toHaveValue(0.65);
    expect(screen.getByText('bge-small-en-v1.5')).toBeInTheDocument(); // read-only embedding.model
    expect(screen.getByLabelText(/default scope/i)).toHaveValue('user:local');
  });

  it('persists an edit via config_set', async () => {
    const spy = vi.spyOn(api, 'tool').mockImplementation((name: string) =>
      (name === 'config_set' ? Promise.resolve({ key: 'search.fts_weight', oldValue: '0.3', newValue: '0.4', written: true }) : Promise.resolve(CONFIG)) as Promise<unknown>);
    render(<Config />);
    const input = await screen.findByLabelText(/fts weight/i);
    await act(async () => {});   // flush any pending React work from initial load
    fireEvent.change(input, { target: { value: '0.4' } });
    await userEvent.click(screen.getAllByRole('button', { name: /save/i })[0]);
    expect(spy).toHaveBeenCalledWith('config_set', { key: 'search.fts_weight', value: 0.4 });
  });

  it('shows OperationProgress and disables Save while config_set is in-flight', async () => {
    vi.spyOn(api, 'tool').mockImplementation((name: string) =>
      name === 'config_set' ? new Promise(() => {})           // never resolves → stays saving
      : Promise.resolve(CONFIG) as Promise<unknown>);
    render(<Config />);
    const input = await screen.findByLabelText(/fts weight/i);   // initial load under REAL timers
    fireEvent.change(input, { target: { value: '0.4' } });       // deterministic value set
    const saveBtn = screen.getAllByRole('button', { name: /save/i })[0];
    vi.useFakeTimers();                                           // BEFORE the click
    try {
      act(() => { fireEvent.click(saveBtn); });                  // synchronous; userEvent hangs under fake timers
      act(() => { vi.advanceTimersByTime(3000); });
      expect(screen.getByRole('status')).toHaveTextContent('Saving… 3s'); // proves timer advanced
      expect(saveBtn).toBeDisabled();
    } finally {
      vi.useRealTimers();
    }
  });
});
