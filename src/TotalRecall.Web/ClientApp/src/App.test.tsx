import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AppShell } from './App';

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AppShell />
    </MemoryRouter>,
  );
}

describe('AppShell routing', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 't', backend: 'sqlite', version: 'x',
    };
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
  });

  it('renders Dashboard at /', () => {
    renderAt('/');
    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
  });

  it('renders Memory with the search term from ?q', () => {
    renderAt('/memory?q=hello');
    expect(screen.getByRole('heading', { name: 'Memory' })).toBeInTheDocument();
    expect(screen.getByText('Search: "hello"')).toBeInTheDocument();
  });

  it('renders Config at /config', () => {
    renderAt('/config');
    expect(screen.getByRole('heading', { name: 'Config' })).toBeInTheDocument();
  });

  it('renders Not found for an unknown route', () => {
    renderAt('/nope');
    expect(screen.getByRole('heading', { name: 'Not found' })).toBeInTheDocument();
  });
});
