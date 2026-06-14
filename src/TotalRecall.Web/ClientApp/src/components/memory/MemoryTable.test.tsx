import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryTable } from './MemoryTable';
import type { MemoryListEntry } from '../../lib/types';

const rows: MemoryListEntry[] = [
  { id: 'a1', tier: 'warm', content_type: 'memory', content: 'use Recharts not uPlot', summary: null, source_tool: 'claude', project: 'tr', tags: [], created_at: 1, updated_at: 1, scope: 'u' },
];
afterEach(() => vi.restoreAllMocks());

describe('MemoryTable', () => {
  it('renders rows and fires onSelect when a row is clicked', async () => {
    const onSelect = vi.fn();
    render(<MemoryTable rows={rows} selectedId={null} onSelect={onSelect} />);
    expect(screen.getByText('use Recharts not uPlot')).toBeInTheDocument();
    expect(screen.getByText('warm')).toBeInTheDocument();
    await userEvent.click(screen.getByText('use Recharts not uPlot'));
    expect(onSelect).toHaveBeenCalledWith('a1');
  });
  it('renders an empty state when there are no rows', () => {
    render(<MemoryTable rows={[]} selectedId={null} onSelect={() => {}} />);
    expect(screen.getByText(/no entries/i)).toBeInTheDocument();
  });
});
