import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { OperationProgress } from './OperationProgress';

describe('OperationProgress', () => {
  it('determinate shows X of N', () => {
    render(<OperationProgress mode="determinate" done={2} total={4} verb="Deleting" />);
    expect(screen.getByRole('status')).toHaveTextContent('Deleting 2 of 4');
  });
  it('indeterminate shows elapsed seconds', () => {
    render(<OperationProgress mode="indeterminate" verb="Running" elapsedMs={14000} />);
    expect(screen.getByRole('status')).toHaveTextContent('Running… 14s');
  });
});
