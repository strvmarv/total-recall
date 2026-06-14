import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { TokenUsageCard } from './TokenUsageCard';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';

function usage(buckets: UsageResult['buckets'], endMs: number): UsageResult {
  const gt = buckets.reduce((a, b) => ({
    session_count: a.session_count + b.session_count, turn_count: a.turn_count + b.turn_count,
    input_tokens: (a.input_tokens ?? 0) + (b.input_tokens ?? 0),
    cache_creation_tokens: (a.cache_creation_tokens ?? 0) + (b.cache_creation_tokens ?? 0),
    cache_read_tokens: (a.cache_read_tokens ?? 0) + (b.cache_read_tokens ?? 0),
    output_tokens: (a.output_tokens ?? 0) + (b.output_tokens ?? 0),
  }), { session_count: 0, turn_count: 0, input_tokens: 0, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 });
  return { query: { start_ms: endMs - 30 * 86400000, end_ms: endMs, group_by: 'day' }, buckets, grand_total: gt, coverage: { sessions_with_full_token_data: 1, sessions_with_partial_token_data: 0, fidelity_percent: 100 } };
}

const render2 = (ui: React.ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>);

describe('TokenUsageCard', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders total tokens and a week-over-week delta', async () => {
    const end = Date.UTC(2026, 5, 14);
    const data = usage([
      { key: '2026-06-02', session_count: 1, turn_count: 1, input_tokens: 100, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 },
      { key: '2026-06-10', session_count: 1, turn_count: 1, input_tokens: 150, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 },
    ], end);
    vi.spyOn(api, 'tool').mockResolvedValue(data);
    render2(<TokenUsageCard refreshKey={0} />);
    expect(await screen.findByText('250')).toBeInTheDocument();
    expect(screen.getByText(/50(\.0)?%/)).toBeInTheDocument();
  });

  it('shows an unavailable state when there is no usage data', async () => {
    const end = Date.UTC(2026, 5, 14);
    vi.spyOn(api, 'tool').mockResolvedValue(usage([], end));
    render2(<TokenUsageCard refreshKey={0} />);
    expect(await screen.findByText(/usage tracking unavailable/i)).toBeInTheDocument();
  });

  it('shows a friendly error when the fetch fails', async () => {
    vi.spyOn(api, 'tool').mockRejectedValue(new Error('boom'));
    render2(<TokenUsageCard refreshKey={0} />);
    expect(await screen.findByText(/load this panel/i)).toBeInTheDocument();
  });

  it('shows an estimated cost from priced models', async () => {
    const end = Date.UTC(2026, 5, 14);
    const data = usage([
      { key: 'claude-sonnet-x', session_count: 1, turn_count: 1, input_tokens: 1_000_000, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 },
    ], end);
    // make the model bucket key drive cost; grand_total still sums tokens
    vi.spyOn(api, 'tool').mockResolvedValue(data);
    render2(<TokenUsageCard refreshKey={0} />);
    expect(await screen.findByText(/\$3\.00/)).toBeInTheDocument(); // 1M sonnet input @ $3
  });
});
