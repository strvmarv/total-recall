import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { KbSearch } from './KbSearch';
import { api } from '../../lib/api';
import type { KbSearchResult } from '../../lib/types';

const RESULT: KbSearchResult = {
  retrievalId: 'r2',
  results: [{ entry: { id: 'k1', content: 'chunk about embeddings', summary: null, source: null, project: null, tags: [], created_at: 1, updated_at: 1, last_accessed_at: 1, access_count: 0, decay_score: 0, scope: 'u' }, score: 0.88, tier: 'cold', content_type: 'knowledge', rank: 1 }],
  hierarchicalMatch: null, needsSummary: false,
};

describe('KbSearch', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('searches the KB and shows hits', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(RESULT);
    render(<KbSearch />);
    await userEvent.type(screen.getByLabelText(/search knowledge base/i), 'embeddings{enter}');
    expect(await screen.findByText(/chunk about embeddings/)).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('kb_search', expect.objectContaining({ query: 'embeddings' }));
  });
});
