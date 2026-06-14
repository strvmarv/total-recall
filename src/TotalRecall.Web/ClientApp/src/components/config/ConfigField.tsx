import { useState, useEffect } from 'react';
import { validateField, type ConfigField as Field, type FieldValue } from '../../lib/configFields';

export function ConfigField({ field, value, onSave }: {
  field: Field;
  value: unknown;
  onSave: (key: string, value: FieldValue) => Promise<void>;
}) {
  const initial: string | boolean = field.type === 'bool' ? Boolean(value) : value == null ? '' : String(value);
  const [draft, setDraft] = useState<string | boolean>(initial);
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved'>('idle');
  const [error, setError] = useState<string | null>(null);
  const dirty = field.type === 'bool' ? draft !== initial : String(draft) !== String(initial);

  useEffect(() => {
    setDraft(field.type === 'bool' ? Boolean(value) : value == null ? '' : String(value));
  }, [value, field.type]);

  async function save() {
    const v = validateField(field, draft);
    if ('error' in v) { setError(v.error); return; }
    setError(null); setStatus('saving');
    try { await onSave(field.key, v.value); setStatus('saved'); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); setStatus('idle'); }
  }

  return (
    <div className="tr-config-field">
      <label htmlFor={field.key}>{field.label} <code className="tr-config-key">{field.key}</code></label>
      <div className="tr-config-control">
        {field.type === 'bool'
          ? <input id={field.key} type="checkbox" checked={Boolean(draft)} onChange={(e) => { setDraft(e.target.checked); setStatus('idle'); }} />
          : field.type === 'string'
          ? <input id={field.key} type="text" value={String(draft)} onChange={(e) => { setDraft(e.target.value); setStatus('idle'); }} />
          : <input id={field.key} type="number" inputMode="decimal" value={String(draft)} onChange={(e) => { setDraft(e.target.value); setStatus('idle'); }} />}
        {dirty && <button type="button" className="tr-btn" onClick={save} disabled={status === 'saving'}>Save</button>}
        {status === 'saved' && !dirty && <span className="tr-config-saved">✓ saved</span>}
      </div>
      {error && <span className="tr-card-error" role="alert">{error}</span>}
    </div>
  );
}
