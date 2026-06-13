import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { TopBar } from './TopBar';

function LocationProbe() {
  const loc = useLocation();
  return <div data-testid="loc">{loc.pathname + loc.search}</div>;
}

function renderTopBar() {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <TopBar />
      <Routes>
        <Route path="*" element={<LocationProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('TopBar', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 't', backend: 'cortex', version: 'x',
    };
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
  });

  it('renders all six nav sections', () => {
    renderTopBar();
    for (const label of ['Dashboard', 'Memory', 'Knowledge Base', 'Usage', '✨ Insights', 'Config']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument();
    }
  });

  it('shows the backend badge from the bootstrap', () => {
    renderTopBar();
    expect(screen.getByText('Cortex')).toBeInTheDocument();
  });

  it('navigates to /memory?q= on search submit', async () => {
    renderTopBar();
    const input = screen.getByRole('searchbox', { name: 'Search memory' });
    await userEvent.type(input, 'pinned rules{enter}');
    expect(screen.getByTestId('loc')).toHaveTextContent('/memory?q=pinned%20rules');
  });
});
