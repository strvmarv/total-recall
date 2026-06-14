import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Usage } from './Usage';
import { api } from '../lib/api';

describe('Usage page', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' };
    vi.spyOn(api, 'tool').mockReturnValue(new Promise(() => {})); // pending: just assert the shell
  });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders the heading, filters, and panels', () => {
    render(<Usage />);
    expect(screen.getByRole('heading', { name: 'Usage', level: 1 })).toBeInTheDocument();
    expect(screen.getByLabelText('Time window')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Usage summary' })).toBeInTheDocument();
  });
});
