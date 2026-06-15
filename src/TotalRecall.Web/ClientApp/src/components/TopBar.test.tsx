import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { TopBar } from './TopBar';

function renderRail() {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <TopBar />
    </MemoryRouter>,
  );
}

describe('SideRail (TopBar)', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 't', backend: 'cortex', version: 'x',
    };
    document.documentElement.dataset.theme = 'dark';
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
  });

  it('renders all six nav sections', () => {
    renderRail();
    for (const label of ['Dashboard', 'Memory', 'Knowledge Base', 'Usage', '✨ Insights', 'Config']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument();
    }
  });

  it('shows the backend badge from the bootstrap', () => {
    renderRail();
    expect(screen.getByTitle(/Storage backend: Cortex/i)).toHaveTextContent('Cortex');
  });

  it('renders the theme toggle', () => {
    renderRail();
    expect(screen.getByRole('button', { name: /theme/i })).toBeInTheDocument();
  });
});
