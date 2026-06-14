import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { KbIngest } from './KbIngest';
import { api } from '../../lib/api';

describe('KbIngest', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('ingests a file and reports chunk count, then calls onIngested', async () => {
    const onIngested = vi.fn();
    const spy = vi.spyOn(api, 'tool').mockResolvedValue({ document_id: 'd1', chunk_count: 7, validation_passed: true });
    render(<KbIngest onIngested={onIngested} />);
    await userEvent.type(screen.getByLabelText(/file path/i), '/tmp/notes.md');
    await userEvent.click(screen.getByRole('button', { name: /ingest file/i }));
    expect(spy).toHaveBeenCalledWith('kb_ingest_file', { path: '/tmp/notes.md' });
    expect(await screen.findByText(/7 chunks/i)).toBeInTheDocument();
    expect(onIngested).toHaveBeenCalled();
  });

  it('surfaces an error when ingest fails', async () => {
    vi.spyOn(api, 'tool').mockRejectedValue(new Error('path does not exist: /nope'));
    render(<KbIngest onIngested={() => {}} />);
    await userEvent.type(screen.getByLabelText(/file path/i), '/nope');
    await userEvent.click(screen.getByRole('button', { name: /ingest file/i }));
    expect(await screen.findByText(/path does not exist/i)).toBeInTheDocument();
  });
});
