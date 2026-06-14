import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { KnowledgeBase } from './KnowledgeBase';
import { api } from '../lib/api';
import type { KbListCollectionsResult } from '../lib/types';

const LIST: KbListCollectionsResult = {
  collections: [{ id: 'c1', name: 'Docs', document_count: 3, chunk_count: 42, created_at: 1, summary: null, source_path: '/x' }],
  count: 1,
};

describe('KnowledgeBase page', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('lists collections from kb_list_collections', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue(LIST);
    render(<KnowledgeBase />);
    expect(await screen.findByText('Docs')).toBeInTheDocument();
    expect(spy).toHaveBeenCalledWith('kb_list_collections');
  });
});
