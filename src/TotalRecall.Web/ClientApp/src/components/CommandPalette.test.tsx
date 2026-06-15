import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { CommandPalette } from './CommandPalette';
import { api } from '../lib/api';

function Probe() { const l = useLocation(); return <div data-testid="loc">{l.pathname + l.search}</div>; }

function renderPalette() {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <CommandPalette />
      <Routes><Route path="*" element={<Probe />} /></Routes>
    </MemoryRouter>,
  );
}

describe('CommandPalette', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'cortex', version: 'x' };
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
    vi.restoreAllMocks();
  });

  it('opens on Ctrl+K and lists page navigation commands', async () => {
    renderPalette();
    await userEvent.keyboard('{Control>}k{/Control}');
    expect(screen.getByRole('dialog', { name: /command palette/i })).toBeInTheDocument();
    expect(screen.getByText('Knowledge Base')).toBeInTheDocument();
  });

  it('navigates to /memory?q= on Enter with a raw query', async () => {
    renderPalette();
    await userEvent.keyboard('{Control>}k{/Control}');
    await userEvent.type(screen.getByRole('combobox'), 'pinned rules{enter}');
    expect(screen.getByTestId('loc')).toHaveTextContent('/memory?q=pinned%20rules');
  });

  it('runs live search for queries >= 2 chars', async () => {
    const spy = vi.spyOn(api, 'tool').mockResolvedValue([] as never);
    renderPalette();
    await userEvent.keyboard('{Control>}k{/Control}');
    await userEvent.type(screen.getByRole('combobox'), 'cortex');
    await waitFor(() => expect(spy).toHaveBeenCalledWith('memory_search', expect.objectContaining({ query: 'cortex' })));
  });
});
