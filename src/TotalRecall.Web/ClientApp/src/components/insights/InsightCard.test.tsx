import { describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { InsightCard } from './InsightCard';
import type { InsightCardActions } from './InsightCard';
import type { InsightCard as Card } from '../../lib/insights';

/** Helper: build a minimal near-dup card with the given deleteIds. */
function nearDupCard(deleteIds: string[]): Extract<Card, { kind: 'near-dup' }> {
  return {
    kind: 'near-dup',
    id: 'near-dup-test',
    icon: '🧬',
    title: 'Near-duplicate cluster',
    impact: 'medium',
    keepId: 'keep',
    keepPreview: 'the one to keep',
    deleteIds,
    topScore: 0.95,
    reviewTo: '/memory',
  };
}

/** Build a minimal InsightCardActions with the given onDeleteCluster. */
function makeActions(onDeleteCluster: InsightCardActions['onDeleteCluster']): InsightCardActions {
  return {
    onDeleteCluster,
    onPin: vi.fn().mockResolvedValue(undefined),
    onApplyThreshold: vi.fn().mockResolvedValue(undefined),
    curvePoints: [],
  };
}

describe('InsightCard — near-dup cluster delete with determinate progress', () => {
  it('shows determinate OperationProgress while onDeleteCluster is in-flight', async () => {
    const card = nearDupCard(['a', 'b']);

    // onDeleteCluster calls onProgress with intermediate values before resolving
    let resolveDelete!: () => void;
    const deletePromise = new Promise<void>((res) => { resolveDelete = res; });

    const onDeleteCluster = vi.fn(
      async (_card: Extract<Card, { kind: 'near-dup' }>, onProgress: (done: number, total: number) => void) => {
        onProgress(1, 2);
        await deletePromise;
        onProgress(2, 2);
      },
    );

    render(
      <MemoryRouter>
        <InsightCard card={card} actions={makeActions(onDeleteCluster)} />
      </MemoryRouter>,
    );

    // Click first button to arm confirm
    await userEvent.click(screen.getByRole('button', { name: /keep newest, delete the rest/i }));

    // Now click the confirm button to start the delete
    await userEvent.click(await screen.findByRole('button', { name: /confirm: delete 2/i }));

    // OperationProgress should appear with the in-flight progress
    const status = await screen.findByRole('status');
    expect(status).toHaveTextContent(/Deleting.*of 2/);

    // Let the delete complete
    resolveDelete();

    // Progress indicator should be gone after completion
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument());

    expect(onDeleteCluster).toHaveBeenCalledOnce();
  });

  it('passes onProgress callback and renders intermediate progress text', async () => {
    const card = nearDupCard(['x', 'y']);

    // Resolve each delete one at a time via captured callbacks
    let capturedOnProgress!: (done: number, total: number) => void;
    let resolveDelete!: () => void;
    const deletePromise = new Promise<void>((res) => { resolveDelete = res; });

    const onDeleteCluster = vi.fn(
      async (_card: Extract<Card, { kind: 'near-dup' }>, onProgress: (done: number, total: number) => void) => {
        capturedOnProgress = onProgress;
        // Immediately fire first progress
        onProgress(1, 2);
        await deletePromise;
      },
    );

    render(
      <MemoryRouter>
        <InsightCard card={card} actions={makeActions(onDeleteCluster)} />
      </MemoryRouter>,
    );

    // Arm the confirm
    await userEvent.click(screen.getByRole('button', { name: /keep newest, delete the rest/i }));
    // Confirm the delete
    await userEvent.click(await screen.findByRole('button', { name: /confirm: delete 2/i }));

    // Should show progress indicator with "Deleting 1 of 2"
    expect(await screen.findByText(/Deleting 1 of 2/)).toBeInTheDocument();

    // Fire second progress (wrapped in act — it triggers a React state update)
    act(() => { capturedOnProgress(2, 2); });
    expect(await screen.findByText(/Deleting 2 of 2/)).toBeInTheDocument();

    // Resolve — progress clears
    resolveDelete();
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument());
  });

  it('clears progress indicator when onDeleteCluster throws', async () => {
    const card = nearDupCard(['z']);

    const onDeleteCluster = vi.fn(
      async (_card: Extract<Card, { kind: 'near-dup' }>, onProgress: (done: number, total: number) => void) => {
        onProgress(0, 1);
        throw new Error('delete failed');
      },
    );

    render(
      <MemoryRouter>
        <InsightCard card={card} actions={makeActions(onDeleteCluster)} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole('button', { name: /keep newest, delete the rest/i }));
    await userEvent.click(await screen.findByRole('button', { name: /confirm: delete 1/i }));

    // error shown
    const alert = await screen.findByText('delete failed');
    expect(alert).toHaveAttribute('role', 'alert');

    // progress indicator must be gone (prog cleared on error)
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
