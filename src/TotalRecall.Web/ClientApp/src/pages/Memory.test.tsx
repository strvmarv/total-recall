import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Memory } from './Memory';
import { api } from '../lib/api';
import type { MemoryListResult } from '../lib/types';

const LIST: MemoryListResult = {
  entries: [{ id: 'a1', tier: 'warm', content_type: 'memory', content: 'pinned rule text', summary: null, source_tool: null, project: null, tags: [], created_at: 1, updated_at: 1, scope: 'u' }],
  count: 1, total: 1, limit: 50, offset: 0,
};

describe('Memory page', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('browses via memory_list and shows results', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(LIST);
    render(<MemoryRouter initialEntries={['/memory']}><Memory /></MemoryRouter>);
    expect(await screen.findByText('pinned rule text')).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('memory_list', expect.anything());
  });

  it('initializes the query from ?q and searches', async () => {
    const hits = [{ entry: { id: 'a1', content: 'hit text', summary: null, source: null, project: null, tags: [], created_at: 1, updated_at: 1, last_accessed_at: 1, access_count: 0, decay_score: 0, scope: 'u' }, score: 0.9, tier: 'warm', content_type: 'memory', rank: 1 }];
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(hits);
    render(<MemoryRouter initialEntries={['/memory?q=hello']}><Memory /></MemoryRouter>);
    expect(await screen.findByText('hit text')).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('memory_search', expect.objectContaining({ query: 'hello' }));
  });
});
