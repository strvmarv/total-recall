export function HealthScore({ score }: { score: number }) {
  const band = score >= 80 ? 'good' : score >= 55 ? 'ok' : 'low';
  return (
    <div className="tr-health" aria-label="Memory health score">
      <div className={`tr-health-num tr-health-${band}`}>{score}</div>
      <div className="tr-health-meta">
        <div className="tr-health-label" id="tr-health-label">Memory health</div>
        <div className="tr-health-bar" role="progressbar" aria-labelledby="tr-health-label" aria-valuenow={score} aria-valuemin={0} aria-valuemax={100}>
          <span style={{ width: `${score}%` }} className={`tr-health-fill tr-health-${band}`} />
        </div>
      </div>
    </div>
  );
}
