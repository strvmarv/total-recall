import { beforeEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ThemeToggle } from './ThemeToggle';

describe('ThemeToggle', () => {
  beforeEach(() => { localStorage.clear(); document.documentElement.dataset.theme = 'dark'; });

  it('toggles theme on click', async () => {
    render(<ThemeToggle />);
    const btn = screen.getByRole('button', { name: /theme/i });
    await userEvent.click(btn);
    expect(document.documentElement.dataset.theme).toBe('light');
    await userEvent.click(btn);
    expect(document.documentElement.dataset.theme).toBe('dark');
  });
});
