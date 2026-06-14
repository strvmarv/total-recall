import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { UsageHeadline } from './UsageHeadline';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';

const end = Date.UTC(2026, 5, 14); const start = end - 7 * 86400000;
const RES: UsageResult = {
  query: { start_ms: start, end_ms: end, group_by: 'model' },
  buckets: [{ key: 'claude-sonnet-x', session_count: 2, turn_count: 10, input_tokens: 1_000_000, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 1_000_000 }],
  grand_total: { session_count: 2, turn_count: 10, input_tokens: 1_000_000, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 1_000_000 },
  coverage: { sessions_with_full_token_data: 2, sessions_with_partial_token_data: 0, fidelity_percent: 100 },
};

describe('UsageHeadline', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('shows totals and estimated cost', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(RES);
    render(<UsageHeadline filters={{ window: '7d', host: '', project: '' }} refreshKey={0} />);
    // 2M total tokens; sonnet 1M in @3 + 1M out @15 = $18 estimated
    expect(await screen.findByText('2,000,000')).toBeInTheDocument();
    expect(screen.getByText(/\$18\.00/)).toBeInTheDocument();
  });
});
