/**
 * Content-shaped loading placeholder: an optional thin top progress bar plus
 * N shimmer rows. Use for async panel/page fetches. Animations are CSS-gated
 * behind prefers-reduced-motion (static blocks when reduced).
 */
export function Skeleton({ rows = 3, bar = false, label = 'Loading…' }:
  { rows?: number; bar?: boolean; label?: string }) {
  return (
    <div className="tr-skeleton" role="status" aria-busy="true" aria-live="polite">
      <span className="tr-visually-hidden">{label}</span>
      {bar && <div className="tr-skeleton-bar" data-testid="tr-skeleton-bar" aria-hidden="true"><i /></div>}
      {Array.from({ length: rows }).map((_, i) => (
        <div key={i} className="tr-skeleton-row" data-testid="tr-skeleton-row" aria-hidden="true" />
      ))}
    </div>
  );
}
