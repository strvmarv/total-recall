import { describe, expect, it, vi } from 'vitest';
import { renderHook, fireEvent } from '@testing-library/react';
import { useHotkey } from './useHotkey';

describe('useHotkey', () => {
  it('fires on Ctrl/Cmd+K and prevents default', () => {
    const onFire = vi.fn();
    renderHook(() => useHotkey('k', onFire));
    const evt = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true });
    fireEvent(window, evt);
    expect(onFire).toHaveBeenCalledTimes(1);
    expect(evt.defaultPrevented).toBe(true);
  });

  it('ignores plain key without modifier', () => {
    const onFire = vi.fn();
    renderHook(() => useHotkey('k', onFire));
    fireEvent(window, new KeyboardEvent('keydown', { key: 'k' }));
    expect(onFire).not.toHaveBeenCalled();
  });
});
