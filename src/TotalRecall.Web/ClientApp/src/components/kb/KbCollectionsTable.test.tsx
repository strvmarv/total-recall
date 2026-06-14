import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { KbCollectionsTable } from './KbCollectionsTable';
import type { KbCollection } from '../../lib/types';

const cols: KbCollection[] = [
  { id: 'c1', name: 'Docs', document_count: 3, chunk_count: 42, created_at: 1, summary: null, source_path: '/x/docs' },
];
afterEach(() => vi.restoreAllMocks());

describe('KbCollectionsTable', () => {
  it('renders collections with counts', () => {
    render(<KbCollectionsTable collections={cols} onChanged={() => {}} />);
    expect(screen.getByText('Docs')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('/x/docs')).toBeInTheDocument();
  });
  it('renders an empty state when there are no collections', () => {
    render(<KbCollectionsTable collections={[]} onChanged={() => {}} />);
    expect(screen.getByText(/no collections/i)).toBeInTheDocument();
  });
});
