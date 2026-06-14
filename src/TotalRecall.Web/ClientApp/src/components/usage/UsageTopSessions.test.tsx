import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { UsageTopSessions } from './UsageTopSessions';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';

const RES: UsageResult = {
  query: { start_ms: 0, end_ms: 1, group_by: 'session' },
  buckets: [{ key: 'sess-1', session_count: 1, turn_count: 12, input_tokens: 9000, cache_creation_tokens: 0, cache_read_tokens: 3000, output_tokens: 1000 }],
  grand_total: { session_count: 1, turn_count: 12, input_tokens: 9000, cache_creation_tokens: 0, cache_read_tokens: 3000, output_tokens: 1000 },
  coverage: { sessions_with_full_token_data: 1, sessions_with_partial_token_data: 0, fidelity_percent: 100 },
};

describe('UsageTopSessions', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('caps the session window to 30d and shows a session row', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(RES);
    render(<UsageTopSessions filters={{ window: '90d', host: '', project: '' }} refreshKey={0} />);
    expect(await screen.findByText(/sess-1/)).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('usage_status', expect.objectContaining({ group_by: 'session', window: '30d', top: 10 }));
  });
});
