import { afterEach, describe, expect, it } from 'vitest';
import { getBootstrap } from './bootstrap';

describe('getBootstrap', () => {
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
  });

  it('returns the injected bootstrap when present', () => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 'abc', backend: 'cortex', version: '9.9.9',
    };
    expect(getBootstrap()).toEqual({ token: 'abc', backend: 'cortex', version: '9.9.9' });
  });

  it('falls back to dev defaults when not injected', () => {
    const b = getBootstrap();
    expect(b.backend).toBe('sqlite');   // default when no VITE_TR_BACKEND
    expect(b.token).toBe('');           // VITE_TR_TOKEN absent -> ''
    expect(b.version).toBe('dev');
  });
});
