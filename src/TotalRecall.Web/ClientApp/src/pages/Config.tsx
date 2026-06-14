import { useState } from 'react';
import { api } from '../lib/api';
import { useAsync } from '../lib/useAsync';
import { ConfigField } from '../components/config/ConfigField';
import { EDITABLE_SECTIONS, READONLY_KEYS, getByPath, type FieldValue } from '../lib/configFields';

interface ConfigResult { config: Record<string, unknown>; }

export function Config() {
  const [refreshKey, setRefreshKey] = useState(0);
  const { data, error, loading } = useAsync<ConfigResult>(() => api.tool<ConfigResult>('config_get'), [refreshKey]);

  async function save(key: string, value: FieldValue) {
    await api.tool('config_set', { key, value });
    setRefreshKey((k) => k + 1); // refetch to reflect the persisted value
  }

  const cfg = data?.config;

  return (
    <section className="tr-config" aria-label="Config">
      <h1>Config</h1>
      {loading && <p className="tr-card-muted">Loading…</p>}
      {error && <p className="tr-card-error" role="alert" title={error}>Couldn't load config.</p>}
      {cfg && (
        <>
          {EDITABLE_SECTIONS.map((sec) => (
            <section className="tr-config-section" key={sec.title}>
              <h2 className="tr-config-title">{sec.title}</h2>
              {sec.fields.map((f) => (
                <ConfigField key={f.key} field={f} value={getByPath(cfg, f.key)} onSave={save} />
              ))}
            </section>
          ))}
          {READONLY_KEYS.map((sec) => (
            <section className="tr-config-section" key={sec.title}>
              <h2 className="tr-config-title">{sec.title}</h2>
              {sec.keys.map((k) => {
                const v = getByPath(cfg, k);
                return (
                  <div className="tr-config-field" key={k}>
                    <label>{k}</label>
                    <span className="tr-config-ro">{v == null ? '—' : String(v)}</span>
                  </div>
                );
              })}
            </section>
          ))}
          <p className="tr-stat-sub">Storage and embedding are read-only — changing them can break the running instance. Edit them in <code>config.toml</code>. (Per-model pricing editing arrives with a future config section.)</p>
        </>
      )}
    </section>
  );
}
