import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { UsageComposition } from './UsageComposition';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';

const RES: UsageResult = {
  query: { start_ms: 0, end_ms: 7 * 86400000, group_by: 'day' },
  buckets: [{ key: '2026-06-10', session_count: 1, turn_count: 1, input_tokens: 100, cache_creation_tokens: 0, cache_read_tokens: 50, output_tokens: 20 }],
  grand_total: { session_count: 1, turn_count: 1, input_tokens: 100, cache_creation_tokens: 0, cache_read_tokens: 50, output_tokens: 20 },
  coverage: { sessions_with_full_token_data: 1, sessions_with_partial_token_data: 0, fidelity_percent: 100 },
};

describe('UsageComposition', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('fetches day buckets and renders the panel title', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(RES);
    render(<UsageComposition filters={{ window: '7d', host: '', project: '' }} refreshKey={0} />);
    expect(await screen.findByRole('heading', { name: /token composition/i })).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('usage_status', expect.objectContaining({ group_by: 'day' }));
  });

  it('empty state when no daily data', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue({ ...RES, buckets: [] });
    render(<UsageComposition filters={{ window: '7d', host: '', project: '' }} refreshKey={0} />);
    expect(await screen.findByText(/no usage in this window/i)).toBeInTheDocument();
  });
});
