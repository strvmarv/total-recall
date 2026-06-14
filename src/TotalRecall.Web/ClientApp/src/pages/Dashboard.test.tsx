import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Dashboard } from './Dashboard';
import { api } from '../lib/api';

describe('Dashboard shell', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 't', backend: 'sqlite', version: 'x',
    };
    // Keep panels in loading state for the shell test (never resolves).
    vi.spyOn(api, 'tool').mockReturnValue(new Promise(() => {}));
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
    vi.restoreAllMocks();
  });

  it('renders the page heading and all six panel titles', () => {
    render(<MemoryRouter><Dashboard /></MemoryRouter>);
    expect(screen.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
    for (const t of ['Tier composition', 'Token usage', 'Retrieval quality', 'Trends', '📌 Pinned directives', '🕒 Recent activity']) {
      expect(screen.getByRole('heading', { name: t })).toBeInTheDocument();
    }
  });

  it('has a manual refresh control', () => {
    render(<MemoryRouter><Dashboard /></MemoryRouter>);
    expect(screen.getByRole('button', { name: /refresh/i })).toBeInTheDocument();
  });
});
