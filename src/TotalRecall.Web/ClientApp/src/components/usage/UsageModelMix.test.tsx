import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { UsageModelMix } from './UsageModelMix';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';

const RES: UsageResult = {
  query: { start_ms: 0, end_ms: 1, group_by: 'model' },
  buckets: [
    { key: 'claude-sonnet-x', session_count: 1, turn_count: 1, input_tokens: 100, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 100 },
    { key: 'claude-opus-4', session_count: 1, turn_count: 1, input_tokens: 50, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 },
  ],
  grand_total: { session_count: 2, turn_count: 2, input_tokens: 150, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 100 },
  coverage: { sessions_with_full_token_data: 2, sessions_with_partial_token_data: 0, fidelity_percent: 100 },
};

describe('UsageModelMix', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('lists models by token share', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(RES);
    render(<UsageModelMix filters={{ window: '7d', host: '', project: '' }} refreshKey={0} />);
    expect(await screen.findByText(/sonnet/i)).toBeInTheDocument();
    expect(screen.getByText(/opus/i)).toBeInTheDocument();
  });
});
