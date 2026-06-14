import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfigField } from './ConfigField';
import type { ConfigField as Field } from '../../lib/configFields';

const f: Field = { key: 'tiers.warm.similarity_threshold', label: 'Similarity threshold', type: 'float', min: 0, max: 1 };

describe('ConfigField', () => {
  it('saves a valid edited value', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<ConfigField field={f} value={0.65} onSave={onSave} />);
    const input = screen.getByLabelText(/similarity threshold/i);
    await userEvent.clear(input);
    await userEvent.type(input, '0.6');
    await userEvent.click(screen.getByRole('button', { name: /save/i }));
    expect(onSave).toHaveBeenCalledWith('tiers.warm.similarity_threshold', 0.6);
  });

  it('blocks an out-of-range value with a field error', async () => {
    const onSave = vi.fn();
    render(<ConfigField field={f} value={0.65} onSave={onSave} />);
    const input = screen.getByLabelText(/similarity threshold/i);
    await userEvent.clear(input);
    await userEvent.type(input, '2');
    await userEvent.click(screen.getByRole('button', { name: /save/i }));
    expect(onSave).not.toHaveBeenCalled();
    expect(screen.getByText(/must be ≤ 1/i)).toBeInTheDocument();
  });
});
