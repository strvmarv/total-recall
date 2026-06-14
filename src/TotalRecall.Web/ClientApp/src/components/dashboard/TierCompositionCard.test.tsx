import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TierCompositionCard } from './TierCompositionCard';
import { api } from '../../lib/api';
import type { StatusResult } from '../../lib/types';

const STATUS: StatusResult = {
  tierSizes: {
    hot_memories: 3, hot_knowledge: 0,
    warm_memories: 2400, warm_knowledge: 66,
    cold_memories: 22000, cold_knowledge: 325,
    pinned_memories: 12, pinned_knowledge: 0,
  },
  knowledgeBase: { collections: [{ id: 'a', name: 'A' }, { id: 'b', name: null }], totalChunks: 22328 },
  db: { path: '/x.db', sizeBytes: 1, sessionId: 's' },
  embedding: { model: 'bge', dimensions: 384 },
};

describe('TierCompositionCard', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders per-tier counts (memories+knowledge) and KB + collections', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(STATUS);
    render(<TierCompositionCard refreshKey={0} />);
    expect(await screen.findByText(/Pinned/)).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();
    expect(screen.getByText('2466')).toBeInTheDocument();
    expect(screen.getByText('22325')).toBeInTheDocument();
    expect(screen.getByText('22328')).toBeInTheDocument();
    expect(screen.getByText(/2 collections/)).toBeInTheDocument();
  });

  it('shows an empty state when everything is zero', async () => {
    const empty: StatusResult = { ...STATUS, tierSizes: { hot_memories: 0, hot_knowledge: 0, warm_memories: 0, warm_knowledge: 0, cold_memories: 0, cold_knowledge: 0, pinned_memories: 0, pinned_knowledge: 0 }, knowledgeBase: { collections: [], totalChunks: 0 } };
    vi.spyOn(api, 'tool').mockResolvedValue(empty);
    render(<TierCompositionCard refreshKey={0} />);
    expect(await screen.findByText(/No memories yet/)).toBeInTheDocument();
  });

  it('shows a friendly error when the fetch fails', async () => {
    vi.spyOn(api, 'tool').mockRejectedValue(new Error('boom'));
    render(<TierCompositionCard refreshKey={0} />);
    expect(await screen.findByText(/Couldn't load this panel/)).toBeInTheDocument();
  });
});
