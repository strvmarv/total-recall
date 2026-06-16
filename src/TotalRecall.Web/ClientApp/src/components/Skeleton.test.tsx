import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Skeleton } from './Skeleton';

describe('Skeleton', () => {
  it('renders the requested number of rows and a progress bar', () => {
    render(<Skeleton rows={3} bar />);
    expect(screen.getByRole('status')).toHaveAttribute('aria-busy', 'true');
    expect(screen.getAllByTestId('tr-skeleton-row')).toHaveLength(3);
    expect(screen.getByTestId('tr-skeleton-bar')).toBeInTheDocument();
  });
});
