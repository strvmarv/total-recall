import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Skeleton } from './Skeleton';

export function Card({ title, headingLevel = 2, drillTo, drillLabel, children }: {
  title: string;
  /** Heading level for the card title; default 2. Set to keep the document outline correct if Card is nested. */
  headingLevel?: 2 | 3 | 4;
  drillTo?: string;
  drillLabel?: string;
  children: ReactNode;
}) {
  const Heading = `h${headingLevel}` as const;
  return (
    <section className="tr-card" aria-label={title}>
      <header className="tr-card-head">
        <Heading className="tr-card-title">{title}</Heading>
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
  if (loading) return <Skeleton rows={3} />;
  if (error) return <p className="tr-card-error" role="alert" title={error}>Couldn't load this panel.</p>;
  if (empty) return <p className="tr-card-muted">{emptyText ?? 'No data yet.'}</p>;
  return <>{children}</>;
}
