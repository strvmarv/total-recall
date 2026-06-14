import { describe, expect, it } from 'vitest';
import { getByPath, validateField, EDITABLE_SECTIONS, type ConfigField } from './configFields';

describe('getByPath', () => {
  it('reads a nested dotted path', () => {
    expect(getByPath({ tiers: { hot: { token_budget: 4000 } } }, 'tiers.hot.token_budget')).toBe(4000);
  });
  it('returns undefined for a missing path', () => {
    expect(getByPath({ a: 1 }, 'a.b.c')).toBeUndefined();
  });
});

describe('validateField', () => {
  const intF: ConfigField = { key: 'k', label: 'k', type: 'int', min: 1, max: 100 };
  const floatF: ConfigField = { key: 'k', label: 'k', type: 'float', min: 0, max: 1 };
  it('parses a valid int', () => { expect(validateField(intF, '5')).toEqual({ value: 5 }); });
  it('rejects a non-number', () => { expect(validateField(intF, 'abc')).toHaveProperty('error'); });
  it('rejects a non-integer for int fields', () => { expect(validateField(intF, '5.5')).toHaveProperty('error'); });
  it('enforces min/max', () => {
    expect(validateField(intF, '0')).toHaveProperty('error');
    expect(validateField(floatF, '1.5')).toHaveProperty('error');
    expect(validateField(floatF, '0.65')).toEqual({ value: 0.65 });
  });
  it('coerces bool', () => { expect(validateField({ key: 'k', label: 'k', type: 'bool' }, true)).toEqual({ value: true }); });
});

describe('EDITABLE_SECTIONS', () => {
  it('excludes embedding and storage keys (read-only)', () => {
    const keys = EDITABLE_SECTIONS.flatMap((s) => s.fields.map((f) => f.key));
    expect(keys.some((k) => k.startsWith('embedding.') || k.startsWith('storage.'))).toBe(false);
    expect(keys).toContain('tiers.warm.similarity_threshold');
  });
});
