import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { KbCollectionsTable } from './KbCollectionsTable';
import { api } from '../../lib/api';
import type { KbCollection } from '../../lib/types';

const cols: KbCollection[] = [
  { id: 'c1', name: 'Docs', document_count: 3, chunk_count: 42, created_at: 1, summary: null, source_path: '/x/docs' },
];

describe('KbCollectionsTable', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders collections with counts', () => {
    render(<KbCollectionsTable collections={cols} onChanged={() => {}} />);
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('/x/docs')).toBeInTheDocument();
  });

  it('renders an empty state when there are no collections', () => {
    render(<KbCollectionsTable collections={[]} onChanged={() => {}} />);
    expect(screen.getByText(/no collections/i)).toBeInTheDocument();
  });

  it('removes a collection after confirm and calls onChanged', async () => {
    const onChanged = vi.fn();
    const spy = vi.spyOn(api, 'tool').mockResolvedValue({ id: 'c1', removed: true, cascaded_count: 42 });
    render(<KbCollectionsTable collections={cols} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole('button', { name: /remove/i }));
    await userEvent.click(screen.getByRole('button', { name: 'Remove' })); // confirm in dialog
    expect(spy).toHaveBeenCalledWith('kb_remove', { id: 'c1' });
  });

  it('refreshes a collection after confirm', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue({ collection_id: 'c1', files: 1, chunks: 42, refreshed: true });
    render(<KbCollectionsTable collections={cols} onChanged={() => {}} />);
    await userEvent.click(screen.getByRole('button', { name: /refresh/i }));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' })); // confirm
    expect(spy).toHaveBeenCalledWith('kb_refresh', { collection: 'c1' });
  });
});
