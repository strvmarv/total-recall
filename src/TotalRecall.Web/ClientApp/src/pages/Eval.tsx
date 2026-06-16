import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Card, CardState } from '../components/Card';
import { OperationProgress } from '../components/OperationProgress';
import { useAsync } from '../lib/useAsync';
import { api } from '../lib/api';
import type {
  EvalReport, EvalBenchmarkResult, EvalCompareResult,
  EvalGrowCandidate, EvalGrowListResult, EvalGrowResolveResult, EvalSnapshotResult,
} from '../lib/types';

const pct = (x: number) => `${Math.round(x * 100)}%`;
const num = (x: number) => x.toLocaleString('en-US');
const ms = (x: number) => `${Math.round(x)} ms`;
const score = (x: number | null) => (x == null ? '—' : x.toFixed(3));
const when = (t: number) => new Date(t).toLocaleString();

const BENCH_ROW_CAP = 50;

export function Eval() {
  const [params] = useSearchParams();
  const grow = params.get('grow') ?? undefined;
  return (
    <section className="tr-eval" aria-label="Eval">
      <h1>Eval</h1>
      <p className="tr-stat-sub tr-eval-intro">Retrieval-quality command center — review the report, run a local benchmark, grow the benchmark from real misses, and compare config snapshots.</p>
      <ReportSection />
      <div className="tr-card-grid">
        <BenchmarkSection />
        <GrowSection focusQuery={grow} />
      </div>
      <CompareSection />
    </section>
  );
}

// ── 1. Report ────────────────────────────────────────────────────────────────
function ReportSection() {
  const [days, setDays] = useState(7);
  const { data, error, loading } = useAsync<EvalReport>(
    () => api.tool<EvalReport>('eval_report', { days }),
    [days],
  );
  const empty = !!data && data.totalEvents === 0;
  const tiers = data?.byTier ? Object.entries(data.byTier) : [];
  const types = data?.byContentType ? Object.entries(data.byContentType) : [];
  const misses = data?.topMisses ?? [];
  const ch = data?.compactionHealth;

  return (
    <Card title="Retrieval report">
      <div className="tr-eval-controls">
        <label htmlFor="tr-eval-window">Report window</label>
        <select id="tr-eval-window" value={String(days)} onChange={(e) => setDays(Number(e.target.value))}>
          <option value="7">Last 7 days</option>
          <option value="30">Last 30 days</option>
        </select>
      </div>
      <CardState loading={loading} error={error} empty={empty} emptyText="No retrieval events in this window.">
        {data && (
          <>
            <div className="tr-usage-cards">
              <Stat figure={pct(data.precision)} sub="precision" />
              <Stat figure={pct(data.hitRate)} sub="hit rate" />
              <Stat figure={pct(data.missRate)} sub="miss rate" />
              <Stat figure={data.mrr.toFixed(3)} sub="MRR" />
              <Stat figure={ms(data.avgLatencyMs)} sub="avg latency" />
              <Stat figure={num(data.totalEvents)} sub="events" />
            </div>

            {tiers.length > 0 && (
              <>
                <h3 className="tr-eval-subhead">By tier</h3>
                <table className="tr-usage-table">
                  <thead><tr><th scope="col">Tier</th><th scope="col" className="num">Precision</th><th scope="col" className="num">Hit rate</th><th scope="col" className="num">Avg score</th><th scope="col" className="num">Count</th></tr></thead>
                  <tbody>
                    {tiers.map(([tier, m]) => (
                      <tr key={tier}>
                        <td>{tier}</td>
                        <td className="num">{pct(m.precision)}</td>
                        <td className="num">{pct(m.hitRate)}</td>
                        <td className="num">{m.avgScore.toFixed(3)}</td>
                        <td className="num">{num(m.count)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}

            {types.length > 0 && (
              <>
                <h3 className="tr-eval-subhead">By content type</h3>
                <table className="tr-usage-table">
                  <thead><tr><th scope="col">Type</th><th scope="col" className="num">Precision</th><th scope="col" className="num">Hit rate</th><th scope="col" className="num">Count</th></tr></thead>
                  <tbody>
                    {types.map(([type, m]) => (
                      <tr key={type}>
                        <td>{type}</td>
                        <td className="num">{pct(m.precision)}</td>
                        <td className="num">{pct(m.hitRate)}</td>
                        <td className="num">{num(m.count)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}

            {misses.length > 0 && (
              <>
                <h3 className="tr-eval-subhead">Top misses</h3>
                <ul className="tr-eval-list">
                  {misses.map((m, i) => (
                    <li key={`${m.query}-${i}`} className="tr-eval-list-row">
                      <span className="tr-eval-query">{m.query}</span>
                      <span className="tr-stat-sub">top score {score(m.topScore)} · {when(m.timestamp)}</span>
                    </li>
                  ))}
                </ul>
              </>
            )}

            {ch && (
              <p className="tr-stat-sub tr-eval-compaction">
                Compaction health: {num(ch.totalCompactions)} compactions · avg preservation {ch.avgPreservationRatio == null ? '—' : pct(ch.avgPreservationRatio)} · {num(ch.entriesWithDrift)} entries with drift.
              </p>
            )}
          </>
        )}
      </CardState>
    </Card>
  );
}

// ── 2. Run benchmark ──────────────────────────────────────────────────────────
function BenchmarkSection() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<EvalBenchmarkResult | null>(null);
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    if (!busy) { setElapsed(0); return; }
    const start = Date.now();
    const t = setInterval(() => setElapsed(Date.now() - start), 250);
    return () => clearInterval(t);
  }, [busy]);

  async function run() {
    setBusy(true); setError(null);
    try { setResult(await api.tool<EvalBenchmarkResult>('eval_benchmark', undefined)); }
    catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  const rows = result?.details ?? [];
  const shown = rows.slice(0, BENCH_ROW_CAP);
  const truncated = rows.length - shown.length;

  return (
    <Card title="Run benchmark">
      <p className="tr-stat-sub">Measures the <strong>local</strong> embedder against the benchmark corpus. Synchronous — may take seconds to tens of seconds.</p>
      <div className="tr-eval-controls">
        <button type="button" className="tr-btn tr-btn-primary" onClick={run} disabled={busy}>
          {busy ? 'Running…' : 'Run benchmark'}
        </button>
        {busy && <OperationProgress mode="indeterminate" verb="Running benchmark" elapsedMs={elapsed} />}
      </div>
      {error && <p className="tr-card-error" role="alert" title={error}>Benchmark failed.</p>}
      {result && (
        <>
          <div className="tr-usage-cards tr-eval-bench-rates">
            <Stat figure={pct(result.exactMatchRate)} sub="exact match" />
            <Stat figure={pct(result.fuzzyMatchRate)} sub="fuzzy match" />
            <Stat figure={pct(result.tierRoutingRate)} sub="tier routing" />
            <Stat figure={pct(result.negativePassRate)} sub="negative pass" />
            <Stat figure={ms(result.avgLatencyMs)} sub="avg latency" />
            <Stat figure={num(result.totalQueries)} sub="queries" />
          </div>
          <table className="tr-usage-table">
            <thead><tr><th scope="col">Query</th><th scope="col">Expected</th><th scope="col">Top result</th><th scope="col" className="num">Score</th><th scope="col">Match</th></tr></thead>
            <tbody>
              {shown.map((d, i) => (
                <tr key={`${d.query}-${i}`}>
                  <td>{d.query}</td>
                  <td>{d.expectedContains}</td>
                  <td title={d.topResult ?? undefined}>{d.topResult ? clip(d.topResult, 40) : '—'}</td>
                  <td className="num">{d.topScore.toFixed(3)}</td>
                  <td>{matchLabel(d)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {truncated > 0 && <p className="tr-stat-sub">+{truncated} more queries not shown.</p>}
        </>
      )}
    </Card>
  );
}

const Pass = () => <span aria-label="passed">✓</span>;
const Fail = () => <span aria-label="failed">✗</span>;

function matchLabel(d: { matched: boolean; fuzzyMatched: boolean; hasNegativeAssertion: boolean; negativePass: boolean }) {
  if (d.hasNegativeAssertion) return <>neg {d.negativePass ? <Pass /> : <Fail />}</>;
  if (d.matched) return <>exact <Pass /></>;
  if (d.fuzzyMatched) return <>fuzzy <Pass /></>;
  return <Fail />;
}

// ── 3. Grow ────────────────────────────────────────────────────────────────
type Choice = 'none' | 'accept' | 'reject';

function GrowSection({ focusQuery }: { focusQuery?: string }) {
  const [refreshKey, setRefreshKey] = useState(0);
  const [choices, setChoices] = useState<Record<string, Choice>>({});
  const [busy, setBusy] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [resolveResult, setResolveResult] = useState<EvalGrowResolveResult | null>(null);
  const [resolveError, setResolveError] = useState<string | null>(null);
  // resolve() bumps refreshKey right after setting resolveResult; skip clearing on that
  // self-triggered pass so the fresh confirmation survives, but clear on any later refresh.
  const keepResultOnNextRefresh = useRef(false);
  const rowRef = useRef<HTMLTableRowElement | null>(null);

  const { data, error, loading } = useAsync<EvalGrowListResult>(
    () => api.tool<EvalGrowListResult>('eval_grow', { action: 'list' }),
    [refreshKey],
  );
  const candidates = data?.candidates ?? [];
  const empty = !!data && candidates.length === 0;

  const norm = (s: string) => s.trim().toLowerCase();
  const match = focusQuery ? candidates.find((c) => norm(c.queryText) === norm(focusQuery)) : undefined;

  useEffect(() => { rowRef.current?.scrollIntoView({ block: 'center' }); }, [focusQuery, data]);

  // Clear any stale resolve confirmation when the candidate list refreshes for a new pass.
  useEffect(() => {
    if (keepResultOnNextRefresh.current) { keepResultOnNextRefresh.current = false; return; }
    setResolveResult(null);
  }, [refreshKey]);

  useEffect(() => {
    if (!busy) { setElapsed(0); return; }
    const start = Date.now();
    const t = setInterval(() => setElapsed(Date.now() - start), 250);
    return () => clearInterval(t);
  }, [busy]);

  const set = (id: string, choice: Choice) =>
    setChoices((prev) => ({ ...prev, [id]: prev[id] === choice ? 'none' : choice }));

  const accept = Object.entries(choices).filter(([, c]) => c === 'accept').map(([id]) => id);
  const reject = Object.entries(choices).filter(([, c]) => c === 'reject').map(([id]) => id);

  async function resolve() {
    setBusy(true); setResolveError(null); setResolveResult(null);
    try {
      const r = await api.tool<EvalGrowResolveResult>('eval_grow', { action: 'resolve', accept, reject });
      setResolveResult(r);
      setChoices({});
      keepResultOnNextRefresh.current = true; // preserve the confirmation across the refresh we trigger next
      setRefreshKey((k) => k + 1); // refresh the candidate list
    } catch (err) { setResolveError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  return (
    <Card title="Grow benchmark">
      <p className="tr-stat-sub">Real low-score queries that could become benchmark cases. Accept to add to the corpus, reject to dismiss.</p>
      {focusQuery && (
        <p className="tr-eval-banner" role="status">
          {`\u{1F3AF} From Insights — `}<strong>{`looking for: ${focusQuery}`}</strong>{`. `}
          {match
            ? `Accept it to add to the benchmark corpus.`
            : <span>{`This query is not a current candidate (different threshold, or already resolved).`}</span>}
        </p>
      )}
      <CardState loading={loading} error={error} empty={empty} emptyText="No grow candidates pending.">
        {data && candidates.length > 0 && (
          <>
            <table className="tr-usage-table">
              <thead><tr><th scope="col">Query</th><th scope="col" className="num">Seen</th><th scope="col" className="num">Top score</th><th scope="col">Decision</th></tr></thead>
              <tbody>
                {candidates.map((c: EvalGrowCandidate) => (
                  <tr key={c.id}
                      ref={match?.id === c.id ? rowRef : undefined}
                      className={match?.id === c.id ? 'tr-row-hl' : undefined}>
                    <td title={c.topResultContent ?? undefined}>{c.queryText}</td>
                    <td className="num">{num(c.timesSeen)}</td>
                    <td className="num">{c.topScore.toFixed(3)}</td>
                    <td>
                      <div className="tr-eval-choice">
                        <button type="button" className={`tr-btn${choices[c.id] === 'accept' ? ' tr-btn-primary' : ''}`} aria-pressed={choices[c.id] === 'accept'} onClick={() => set(c.id, 'accept')}>Accept</button>
                        <button type="button" className={`tr-btn${choices[c.id] === 'reject' ? ' tr-btn-danger' : ''}`} aria-pressed={choices[c.id] === 'reject'} onClick={() => set(c.id, 'reject')}>Reject</button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="tr-eval-controls">
              <button type="button" className="tr-btn tr-btn-primary" onClick={resolve} disabled={busy || (accept.length === 0 && reject.length === 0)}>
                {busy ? 'Resolving…' : `Resolve (${accept.length} accept · ${reject.length} reject)`}
              </button>
              {busy && <OperationProgress mode="indeterminate" verb="Resolving" elapsedMs={elapsed} />}
            </div>
          </>
        )}
      </CardState>
      {/* Rendered as siblings of CardState so the list re-fetch (which collapses CardState to a
          loading fallback) can't tear down a fresh resolve confirmation or its error. */}
      {resolveError && <p className="tr-card-error" role="alert" title={resolveError}>Resolve failed.</p>}
      {resolveResult && (
        <p className="tr-stat-sub" role="status">
          Accepted {num(resolveResult.accepted)} · rejected {num(resolveResult.rejected)}. Benchmark written to <code>{resolveResult.benchmarkPath}</code>.
        </p>
      )}
    </Card>
  );
}

// ── 4. Compare + Snapshot ──────────────────────────────────────────────────────
function CompareSection() {
  const [before, setBefore] = useState('');
  const [after, setAfter] = useState('latest');
  const [busy, setBusy] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<EvalCompareResult | null>(null);

  const [snapName, setSnapName] = useState('');
  const [snapBusy, setSnapBusy] = useState(false);
  const [snapElapsed, setSnapElapsed] = useState(0);
  const [snapError, setSnapError] = useState<string | null>(null);
  const [snapResult, setSnapResult] = useState<EvalSnapshotResult | null>(null);

  useEffect(() => {
    if (!busy) { setElapsed(0); return; }
    const start = Date.now();
    const t = setInterval(() => setElapsed(Date.now() - start), 250);
    return () => clearInterval(t);
  }, [busy]);

  useEffect(() => {
    if (!snapBusy) { setSnapElapsed(0); return; }
    const start = Date.now();
    const t = setInterval(() => setSnapElapsed(Date.now() - start), 250);
    return () => clearInterval(t);
  }, [snapBusy]);

  async function compare() {
    setBusy(true); setError(null); setResult(null);
    try {
      const args: Record<string, unknown> = { after: after.trim() || 'latest' };
      if (before.trim()) args.before = before.trim();
      setResult(await api.tool<EvalCompareResult>('eval_compare', args));
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  async function snapshot() {
    if (!snapName.trim()) return;
    setSnapBusy(true); setSnapError(null); setSnapResult(null);
    try {
      const r = await api.tool<EvalSnapshotResult>('eval_snapshot', { name: snapName.trim() });
      setSnapResult(r);
      setSnapName('');
    } catch (err) { setSnapError(err instanceof Error ? err.message : String(err)); }
    finally { setSnapBusy(false); }
  }

  return (
    <Card title="Compare snapshots">
      <p className="tr-stat-sub">Compare two config snapshots by id. You must enter a <em>before</em> snapshot id; <em>after</em> defaults to <code>latest</code>. There is no snapshot list yet, so enter ids as free text.</p>

      <div className="tr-eval-form">
        <div className="tr-field">
          <label htmlFor="tr-eval-before">Before</label>
          <input id="tr-eval-before" className="tr-input" value={before} onChange={(e) => setBefore(e.target.value)} placeholder="snapshot id (required)" />
        </div>
        <div className="tr-field">
          <label htmlFor="tr-eval-after">After</label>
          <input id="tr-eval-after" className="tr-input" value={after} onChange={(e) => setAfter(e.target.value)} placeholder="latest" />
        </div>
        <button type="button" className="tr-btn tr-btn-primary" onClick={compare} disabled={busy || !before.trim()}>{busy ? 'Comparing…' : 'Compare'}</button>
        {busy && <OperationProgress mode="indeterminate" verb="Comparing" elapsedMs={elapsed} />}
      </div>
      {error && <p className="tr-card-error" role="alert" title={error}>Compare failed.</p>}
      {result && (
        <>
          {result.warning && <p className="tr-card-error tr-eval-warning" role="alert">{result.warning}</p>}
          <p className="tr-stat-sub">{result.beforeId} → {result.afterId}</p>
          <div className="tr-usage-cards tr-eval-deltas">
            <Stat figure={signed(result.deltas.precision, pct)} sub="Δ precision (higher is better)" valence={valence(result.deltas.precision, true)} />
            <Stat figure={signed(result.deltas.hitRate, pct)} sub="Δ hit rate (higher is better)" valence={valence(result.deltas.hitRate, true)} />
            <Stat figure={signed(result.deltas.mrr, (x) => x.toFixed(3))} sub="Δ MRR (higher is better)" valence={valence(result.deltas.mrr, true)} />
            <Stat figure={signed(result.deltas.missRate, pct)} sub="Δ miss rate (lower is better)" valence={valence(result.deltas.missRate, false)} />
            <Stat figure={signed(result.deltas.avgLatencyMs, (x) => `${Math.round(x)} ms`)} sub="Δ latency (lower is better)" valence={valence(result.deltas.avgLatencyMs, false)} />
          </div>
          <div className="tr-eval-change-cols">
            <ChangeList title="Regressions" items={result.regressions} />
            <ChangeList title="Improvements" items={result.improvements} />
          </div>
        </>
      )}

      <h3 className="tr-eval-subhead">Snapshot</h3>
      <div className="tr-eval-form">
        <div className="tr-field">
          <label htmlFor="tr-eval-snap">Snapshot name</label>
          <input id="tr-eval-snap" className="tr-input" value={snapName} onChange={(e) => setSnapName(e.target.value)} placeholder="e.g. baseline" />
        </div>
        <button type="button" className="tr-btn" onClick={snapshot} disabled={snapBusy || !snapName.trim()}>{snapBusy ? 'Snapshotting…' : 'Snapshot'}</button>
        {snapBusy && <OperationProgress mode="indeterminate" verb="Snapshotting" elapsedMs={snapElapsed} />}
      </div>
      {snapError && <p className="tr-card-error" role="alert" title={snapError}>Snapshot failed.</p>}
      {snapResult && (
        <p className="tr-stat-sub" role="status">
          Snapshot <code>{snapResult.id}</code> ({snapResult.name}){snapResult.deduped ? ' — deduped (matched an existing snapshot)' : ''}.
        </p>
      )}
    </Card>
  );
}

function ChangeList({ title, items }: { title: string; items: EvalCompareResult['regressions'] }) {
  return (
    <div>
      <h4 className="tr-eval-subhead">{title} ({items.length})</h4>
      {items.length === 0
        ? <p className="tr-stat-sub">None.</p>
        : <ul className="tr-eval-list">
            {items.map((c, i) => (
              <li key={`${c.queryText}-${i}`} className="tr-eval-list-row">
                <span className="tr-eval-query">{c.queryText}</span>
                <span className="tr-stat-sub">{c.beforeOutcome} → {c.afterOutcome} · {score(c.beforeScore)} → {score(c.afterScore)}</span>
              </li>
            ))}
          </ul>}
    </div>
  );
}

// ── shared bits ────────────────────────────────────────────────────────────
function Stat({ figure, sub, valence }: { figure: string; sub: string; valence?: 'good' | 'bad' | null }) {
  const valenceClass = valence ? ` tr-valence-${valence}` : '';
  return <div className="tr-usage-stat"><div className={`tr-stat-figure${valenceClass}`}>{figure}</div><div className="tr-stat-sub">{sub}</div></div>;
}

const clip = (s: string, n: number) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);
const signed = (x: number, fmt: (n: number) => string) => `${x > 0 ? '+' : ''}${fmt(x)}`;

/**
 * Valence of a delta given which direction is desirable.
 * `higherIsBetter` metrics (precision/hitRate/mrr): positive delta is good.
 * `lowerIsBetter` metrics (missRate/avgLatencyMs): positive delta is bad.
 * A zero delta is neutral (no color).
 */
const valence = (delta: number, higherIsBetter: boolean): 'good' | 'bad' | null => {
  if (delta === 0) return null;
  const improved = higherIsBetter ? delta > 0 : delta < 0;
  return improved ? 'good' : 'bad';
};
