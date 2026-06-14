import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { UsageBreakdown } from './UsageBreakdown';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';

const RES: UsageResult = {
  query: { start_ms: 0, end_ms: 1, group_by: 'project' },
  buckets: [{ key: 'total-recall', session_count: 3, turn_count: 30, input_tokens: 1000, cache_creation_tokens: 0, cache_read_tokens: 200, output_tokens: 500 }],
  grand_total: { session_count: 3, turn_count: 30, input_tokens: 1000, cache_creation_tokens: 0, cache_read_tokens: 200, output_tokens: 500 },
  coverage: { sessions_with_full_token_data: 3, sessions_with_partial_token_data: 0, fidelity_percent: 100 },
};

describe('UsageBreakdown', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders a per-project row with token totals', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(RES);
    render(<UsageBreakdown filters={{ window: '7d', host: '', project: '' }} refreshKey={0} />);
    expect(await screen.findByText('total-recall')).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('usage_status', expect.objectContaining({ group_by: 'project' }));
  });
});
