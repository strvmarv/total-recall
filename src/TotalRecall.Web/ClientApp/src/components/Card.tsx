import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';

export function Card({ title, drillTo, drillLabel, children }: {
  title: string;
  drillTo?: string;
  drillLabel?: string;
  children: ReactNode;
}) {
  return (
    <section className="tr-card" aria-label={title}>
      <header className="tr-card-head">
        <h2 className="tr-card-title">{title}</h2>
        {drillTo && <Link className="tr-card-drill" to={drillTo}>{drillLabel ?? 'view →'}</Link>}
      </header>
      <div className="tr-card-body">{children}</div>
    </section>
  );
}

/**
 * Renders loading / error / empty fallbacks, else children. The raw error
 * detail is kept off-screen (title attr) so users see a friendly message, not
 * a raw response body.
 */
export function CardState({ loading, error, empty, emptyText, children }: {
  loading: boolean;
  error: string | null;
  empty?: boolean;
  emptyText?: string;
  children: ReactNode;
}) {
  if (loading) return <p className="tr-card-muted">Loading…</p>;
  if (error) return <p className="tr-card-error" role="alert" title={error}>Couldn't load this panel.</p>;
  if (empty) return <p className="tr-card-muted">{emptyText ?? 'No data yet.'}</p>;
  return <>{children}</>;
}
