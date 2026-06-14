import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryDetail } from './MemoryDetail';
import { api } from '../../lib/api';
import type { MemoryInspectResult, LineageNode } from '../../lib/types';

const INSPECT: MemoryInspectResult = {
  id: 'a1', tier: 'warm', content_type: 'memory', content: 'full content here', summary: 's',
  source: null, source_tool: 'claude', project: 'tr', tags: ['x'], created_at: 1, updated_at: 2,
  last_accessed_at: 3, access_count: 5, decay_score: 0.42, parent_id: null, collection_id: null,
  metadata: '{}', compaction_history: null,
};
const LINEAGE: LineageNode = { id: 'a1', compaction_log_id: null, reason: null, timestamp: null, source_tier: null, target_tier: null, sources: null };

describe('MemoryDetail', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('shows inspect fields for the selected entry', async () => {
    vi.spyOn(api, 'tool').mockImplementation((name: string) =>
      Promise.resolve(name === 'memory_inspect' ? INSPECT : LINEAGE) as Promise<unknown>);
    render(<MemoryDetail id="a1" onClose={() => {}} onChanged={() => {}} />);
    expect(await screen.findByText('full content here')).toBeInTheDocument();
    expect(screen.getByText(/access count/i)).toBeInTheDocument();
    expect(screen.getByText('5')).toBeInTheDocument();
  });

  it('deletes after confirm and calls onChanged', async () => {
    const onChanged = vi.fn();
    const spy = vi.spyOn(api, 'tool').mockImplementation((name: string) => {
      if (name === 'memory_inspect') return Promise.resolve(INSPECT) as Promise<unknown>;
      if (name === 'memory_lineage') return Promise.resolve(LINEAGE) as Promise<unknown>;
      return Promise.resolve({ deleted: true }) as Promise<unknown>;
    });
    const { default: userEvent } = await import('@testing-library/user-event');
    render(<MemoryDetail id="a1" onClose={() => {}} onChanged={onChanged} />);
    await screen.findByText('full content here');
    await userEvent.click(screen.getByRole('button', { name: /delete/i }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete' })); // confirm in dialog
    expect(spy).toHaveBeenCalledWith('memory_delete', { id: 'a1' });
  });
});
