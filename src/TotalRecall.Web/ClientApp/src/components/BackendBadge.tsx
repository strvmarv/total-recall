import { getBootstrap } from '../lib/bootstrap';

const LABELS: Record<string, string> = {
  sqlite: 'SQLite',
  postgres: 'Postgres',
  cortex: 'Cortex',
};

export function BackendBadge() {
  const { backend } = getBootstrap();
  const key = (backend ?? '').toLowerCase();
  const label = LABELS[key] ?? backend ?? 'unknown';
  const suffix = key === 'sqlite' ? ' · local' : '';
  return (
    <span className={`tr-badge tr-badge-${key || 'unknown'}`} title={`Storage backend: ${label}`}>
      <span className="tr-badge-dot" aria-hidden="true" />
      {label}
      {suffix}
    </span>
  );
}
