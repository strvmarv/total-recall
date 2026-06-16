/**
 * Feedback for a user-triggered operation. Render inline next to a busy
 * trigger. `determinate` shows "verb X of N" + a progress bar; `indeterminate`
 * shows "verb… Ns" with a live elapsed counter (caller owns the timer).
 */
export function OperationProgress(props:
  | { mode: 'determinate'; done: number; total: number; verb: string }
  | { mode: 'indeterminate'; verb: string; elapsedMs: number }) {
  if (props.mode === 'determinate') {
    const pct = props.total > 0 ? Math.round((props.done / props.total) * 100) : 0;
    return (
      <span className="tr-opprog" role="status" aria-live="polite">
        <span className="tr-spinner" aria-hidden="true" />
        {props.verb} {props.done} of {props.total}
        <span className="tr-opprog-bar" aria-hidden="true"><i style={{ width: `${pct}%` }} /></span>
      </span>
    );
  }
  const secs = Math.round(props.elapsedMs / 1000);
  return (
    <span className="tr-opprog" role="status" aria-live="polite">
      <span className="tr-spinner" aria-hidden="true" />
      {props.verb}… {secs}s
    </span>
  );
}
