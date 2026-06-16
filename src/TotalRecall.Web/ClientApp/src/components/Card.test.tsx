import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { CardState } from './Card';

describe('CardState', () => {
  it('shows a skeleton while loading', () => {
    render(<CardState loading={true} error={null}>content</CardState>);
    expect(screen.getByRole('status')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows an error message when error is provided', () => {
    render(<CardState loading={false} error="Something went wrong">content</CardState>);
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });

  it('shows children when not loading or errored', () => {
    render(<CardState loading={false} error={null}>content</CardState>);
    expect(screen.getByText('content')).toBeInTheDocument();
  });

  it('shows empty text when empty is true', () => {
    render(<CardState loading={false} error={null} empty emptyText="Nothing here">content</CardState>);
    expect(screen.getByText('Nothing here')).toBeInTheDocument();
  });
});
