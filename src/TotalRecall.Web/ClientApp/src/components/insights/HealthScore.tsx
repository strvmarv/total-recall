import { useState } from 'react';
import type { InsightsHealthBreakdown, InsightsHealthComponent } from '../../lib/types';

const COMPONENTS: { key: keyof InsightsHealthBreakdown; label: string }[] = [
  { key: 'retrieval', label: 'Retrieval' },
  { key: 'capture', label: 'Capture mix' },
  { key: 'pinned', label: 'Pinned discipline' },
  { key: 'kb', label: 'Knowledge base' },
];

export function HealthScore({ score, breakdown }: { score: number; breakdown: InsightsHealthBreakdown }) {
  const [open, setOpen] = useState(false);
  const band = score >= 80 ? 'good' : score >= 55 ? 'ok' : 'low';
  return (
    <div className="tr-health" aria-label="Memory health score">
      <div className="tr-health-top">
        <div className={`tr-health-num tr-health-${band}`}>{score}</div>
        <div className="tr-health-meta">
          <div className="tr-health-label" id="tr-health-label">Memory health</div>
          <div className="tr-health-bar" role="progressbar" aria-labelledby="tr-health-label" aria-valuenow={score} aria-valuemin={0} aria-valuemax={100}>
            <span style={{ width: `${score}%` }} className={`tr-health-fill tr-health-${band}`} />
          </div>
          <button
            type="button"
            className="tr-health-toggle"
            aria-expanded={open}
            onClick={() => setOpen((v) => !v)}
          >
            {open ? '▾ Hide breakdown' : '▸ Show breakdown'}
          </button>
        </div>
      </div>
      {open && (
        <ul className="tr-health-breakdown">
          {COMPONENTS.map(({ key, label }) => {
            const c: InsightsHealthComponent = breakdown[key];
            return (
              <li className="tr-health-row" key={key}>
                <span className="tr-health-row-label">{label}</span>
                <span className="tr-health-row-score">{c.score}/{c.max}</span>
                <span className="tr-health-row-detail">{c.detail}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
