import { useState } from 'react';
import { Link } from 'react-router-dom';
import { CartesianGrid, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import type { InsightCard as Card } from '../../lib/insights';
import { fmtScore } from '../../lib/insights';
import type { InsightsThresholdPoint } from '../../lib/types';
import { useChartTheme } from '../../lib/chartTheme';
import { OperationProgress } from '../OperationProgress';

export interface InsightCardActions {
  /** near-dup: delete every id in `deleteIds` (one tool call each), then refetch. */
  onDeleteCluster: (card: Extract<Card, { kind: 'near-dup' }>, onProgress: (done: number, total: number) => void) => Promise<void>;
  /** pin: memory_pin the entry, then refetch. */
  onPin: (card: Extract<Card, { kind: 'pin' }>) => Promise<void>;
  /** threshold: config_set the suggested value, then refetch. */
  onApplyThreshold: (card: Extract<Card, { kind: 'threshold' }>) => Promise<void>;
  /** Curve points for the threshold chart (from data.thresholdCurve.points). */
  curvePoints: InsightsThresholdPoint[];
}

function Shell({ card, children }: { card: Card; children: React.ReactNode }) {
  return (
    <article className="tr-insight" aria-label={card.title}>
      <div className="tr-insight-icon" aria-hidden="true">{card.icon}</div>
      <div className="tr-insight-body">
        <div className="tr-insight-head">
          <h3>{card.title}</h3>
          <span className={`tr-impact tr-impact-${card.impact}`}>{card.impact}</span>
        </div>
        {children}
      </div>
    </article>
  );
}

export function InsightCard({ card, actions }: { card: Card; actions: InsightCardActions }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // near-dup destructive two-step confirm
  const [confirming, setConfirming] = useState(false);
  // near-dup delete progress
  const [prog, setProg] = useState<{ done: number; total: number } | null>(null);

  async function run(fn: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  if (card.kind === 'near-dup') {
    return (
      <Shell card={card}>
        <p className="tr-insight-evidence">
          Keep newest: <span className="tr-insight-preview">{card.keepPreview}</span>
        </p>
        <p className="tr-insight-evidence">
          {card.deleteIds.length} older {card.deleteIds.length === 1 ? 'duplicate' : 'duplicates'} · similarity {fmtScore(card.topScore)}
        </p>
        <div className="tr-insight-actions">
          {confirming ? (
            <>
              <button
                type="button"
                className="tr-btn tr-btn-danger"
                disabled={busy}
                onClick={() => run(async () => {
                  setProg({ done: 0, total: card.deleteIds.length });
                  try {
                    await actions.onDeleteCluster(card, (done, total) => setProg({ done, total }));
                    setConfirming(false);
                  } finally {
                    setProg(null);
                  }
                })}
              >
                Confirm: delete {card.deleteIds.length}?
              </button>
              <button type="button" className="tr-btn" disabled={busy} onClick={() => setConfirming(false)}>Cancel</button>
            </>
          ) : (
            <button
              type="button"
              className="tr-btn tr-btn-danger"
              disabled={busy}
              onClick={() => { setError(null); setConfirming(true); }}
            >
              Keep newest, delete the rest
            </button>
          )}
          <Link className="tr-btn" to={card.reviewTo}>Review</Link>
        </div>
        {busy && prog && <OperationProgress mode="determinate" done={prog.done} total={prog.total} verb="Deleting" />}
        {error && <p className="tr-card-error" role="alert">{error}</p>}
      </Shell>
    );
  }

  if (card.kind === 'pin') {
    return (
      <Shell card={card}>
        <p className="tr-insight-evidence">
          <span className="tr-insight-preview">{card.preview}</span>
        </p>
        <p className="tr-insight-evidence">
          accessed {card.accessCount}× · {card.tier} tier
        </p>
        <div className="tr-insight-actions">
          <button
            type="button"
            className="tr-btn tr-btn-primary"
            disabled={busy}
            onClick={() => run(() => actions.onPin(card))}
          >
            Pin
          </button>
        </div>
        {error && <p className="tr-card-error" role="alert">{error}</p>}
      </Shell>
    );
  }

  if (card.kind === 'gap') {
    return (
      <Shell card={card}>
        <p className="tr-insight-evidence">
          <span className="tr-insight-preview">{card.query}</span>
        </p>
        <p className="tr-insight-evidence">
          seen {card.timesSeen}× · top score {card.topScore == null ? '—' : fmtScore(card.topScore)}
        </p>
        <div className="tr-insight-actions">
          <Link className="tr-btn tr-btn-primary" to={card.to}>Open in Eval</Link>
        </div>
      </Shell>
    );
  }

  if (card.kind === 'threshold') {
    return (
      <Shell card={card}>
        <p className="tr-insight-evidence">
          similarity threshold <strong>{fmtScore(card.current)}</strong> → <strong>{fmtScore(card.suggested)}</strong>
        </p>
        <ThresholdChart points={actions.curvePoints} current={card.current} suggested={card.suggested} />
        <div className="tr-insight-actions">
          <button
            type="button"
            className="tr-btn tr-btn-primary"
            disabled={busy}
            onClick={() => run(() => actions.onApplyThreshold(card))}
          >
            Apply
          </button>
        </div>
        {error && <p className="tr-card-error" role="alert">{error}</p>}
      </Shell>
    );
  }

  // cost-spike
  return (
    <Shell card={card}>
      <p className="tr-insight-evidence">{card.evidence}</p>
      <div className="tr-insight-actions">
        <Link className="tr-btn tr-btn-primary" to={card.to}>Open Usage</Link>
      </div>
    </Shell>
  );
}

function ThresholdChart({ points, current, suggested }: { points: InsightsThresholdPoint[]; current: number; suggested: number }) {
  const theme = useChartTheme();
  if (points.length === 0) return null;
  const data = points.map((p) => ({ threshold: p.threshold, hitRate: p.hitRate, precision: p.precision }));
  return (
    <div className="tr-insight-chart" aria-hidden="true" style={{ width: '100%', height: 120, marginTop: 'var(--tr-space-2)' }}>
      <ResponsiveContainer>
        <LineChart data={data} margin={{ top: 6, right: 8, bottom: 2, left: -16 }}>
          <CartesianGrid stroke={theme.grid} strokeDasharray="2 2" />
          <XAxis dataKey="threshold" tick={{ fill: theme.tick, fontFamily: theme.mono, fontSize: 10 }} stroke={theme.grid} />
          <YAxis domain={[0, 1]} tick={{ fill: theme.tick, fontFamily: theme.mono, fontSize: 10 }} stroke={theme.grid} width={36} />
          <Tooltip
            contentStyle={{ background: 'var(--tr-surface)', border: '1px solid var(--tr-border)', borderRadius: 'var(--tr-radius)', fontFamily: theme.mono, fontSize: 11 }}
            labelStyle={{ color: 'var(--tr-text-muted)' }}
          />
          {/* Reference markers use tier colors distinct from the data lines
              (hitRate=tierKb green, precision=accent) so they stay readable at the intersection. */}
          <ReferenceLine x={current} stroke={theme.tierHot} strokeDasharray="3 3" />
          <ReferenceLine x={suggested} stroke={theme.tierWarm} />
          <Line type="monotone" dataKey="hitRate" stroke={theme.tierKb} dot={false} isAnimationActive={false} />
          <Line type="monotone" dataKey="precision" stroke={theme.accent} dot={false} isAnimationActive={false} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
