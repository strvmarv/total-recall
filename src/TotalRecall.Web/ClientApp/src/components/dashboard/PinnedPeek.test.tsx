import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { PinnedPeek } from './PinnedPeek';
import { api } from '../../lib/api';
import type { MemoryListResult } from '../../lib/types';

const LIST: MemoryListResult = {
  entries: [
    { id: '1', tier: 'pinned', content_type: 'memory', content: 'Never add Co-Authored-By trailers', summary: null, source_tool: null, project: 'total-recall', tags: [], created_at: 1, updated_at: 1, scope: 'user' },
  ],
  count: 1, total: 12, limit: 8, offset: 0,
};
const render2 = (ui: React.ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>);

describe('PinnedPeek', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('lists pinned entries with the total count', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(LIST);
    render2(<PinnedPeek refreshKey={0} />);
    expect(await screen.findByText(/Never add Co-Authored-By/)).toBeInTheDocument();
    expect(screen.getByText(/12/)).toBeInTheDocument();
  });

  it('shows an empty state when nothing is pinned', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue({ entries: [], count: 0, total: 0, limit: 8, offset: 0 });
    render2(<PinnedPeek refreshKey={0} />);
    expect(await screen.findByText(/No pinned directives/i)).toBeInTheDocument();
  });

  it('shows a friendly error when the fetch fails', async () => {
    vi.spyOn(api, 'tool').mockRejectedValue(new Error('boom'));
    render2(<PinnedPeek refreshKey={0} />);
    expect(await screen.findByText(/load this panel/i)).toBeInTheDocument();
  });
});
