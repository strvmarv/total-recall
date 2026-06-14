export function HealthScore({ score }: { score: number }) {
  const band = score >= 80 ? 'good' : score >= 55 ? 'ok' : 'low';
  return (
    <div className="tr-health" aria-label="Memory health score">
      <div className={`tr-health-num tr-health-${band}`}>{score}</div>
      <div className="tr-health-meta">
        <div className="tr-health-label">Memory health</div>
        <div className="tr-health-bar"><span style={{ width: `${score}%` }} className={`tr-health-fill tr-health-${band}`} /></div>
      </div>
    </div>
  );
}
