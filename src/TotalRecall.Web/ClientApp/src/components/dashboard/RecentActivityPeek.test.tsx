import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { RecentActivityPeek } from './RecentActivityPeek';
import { api } from '../../lib/api';
import type { MemoryRecentResult } from '../../lib/types';

const RECENT: MemoryRecentResult = {
  entries: [
    { id: '1', tier: 'warm', entry_type: 'correction', project: 'tr', created_at: Date.now() - 60_000, updated_at: 0, last_accessed_at: 0, preview: 'use Recharts not uPlot' },
  ],
  count: 1, order: 'created',
};
const render2 = (ui: React.ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>);

describe('RecentActivityPeek', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders a recent row with its action tag and preview', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(RECENT);
    render2(<RecentActivityPeek refreshKey={0} />);
    expect(await screen.findByText(/use Recharts not uPlot/)).toBeInTheDocument();
    expect(screen.getByText('correction')).toBeInTheDocument();
  });

  it('shows an empty state when there is no recent activity', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue({ entries: [], count: 0, order: 'created' });
    render2(<RecentActivityPeek refreshKey={0} />);
    expect(await screen.findByText(/No recent activity/i)).toBeInTheDocument();
  });
});
