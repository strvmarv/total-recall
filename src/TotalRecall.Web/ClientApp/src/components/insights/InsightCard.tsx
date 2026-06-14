import { Link } from 'react-router-dom';
import type { Suggestion } from '../../lib/insights';

export function InsightCard({ s }: { s: Suggestion }) {
  return (
    <article className="tr-insight" aria-label={s.title}>
      <div className="tr-insight-icon" aria-hidden="true">{s.icon}</div>
      <div className="tr-insight-body">
        <div className="tr-insight-head">
          <h3>{s.title}</h3>
          <span className={`tr-impact tr-impact-${s.impact}`}>{s.impact}</span>
        </div>
        <p className="tr-insight-evidence">{s.evidence}</p>
      </div>
      {s.action.kind === 'navigate' && <Link className="tr-btn tr-btn-primary" to={s.action.to}>{s.action.label}</Link>}
    </article>
  );
}
