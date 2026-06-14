import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TrendsCard } from './TrendsCard';
import { api } from '../../lib/api';
import type { MemoryHistoryResult } from '../../lib/types';

const t = Date.UTC(2026, 5, 10);
const HISTORY: MemoryHistoryResult = {
  movements: [
    { id: '1', timestamp: t, session_id: null, source_tier: 'hot', target_tier: 'warm', source_entry_ids: [], target_entry_id: null, reason: 'decay' },
    { id: '2', timestamp: t, session_id: null, source_tier: 'hot', target_tier: 'warm', source_entry_ids: [], target_entry_id: null, reason: 'decay' },
    { id: '3', timestamp: t, session_id: null, source_tier: 'warm', target_tier: 'cold', source_entry_ids: [], target_entry_id: null, reason: 'decay' },
  ],
  count: 3,
};

describe('TrendsCard', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders total movements and the top transition', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(HISTORY);
    render(<TrendsCard refreshKey={0} />);
    expect(await screen.findByText(/3 movements/)).toBeInTheDocument();
    expect(screen.getByText('hot → warm')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
  });

  it('shows an empty state when there is no compaction activity', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue({ movements: [], count: 0 });
    render(<TrendsCard refreshKey={0} />);
    expect(await screen.findByText(/No compaction activity yet/i)).toBeInTheDocument();
  });
});
