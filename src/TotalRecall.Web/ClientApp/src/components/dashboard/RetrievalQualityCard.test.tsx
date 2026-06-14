import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { RetrievalQualityCard } from './RetrievalQualityCard';
import { api } from '../../lib/api';
import type { EvalReport } from '../../lib/types';

const REPORT: EvalReport = { precision: 0.8, hitRate: 0.75, missRate: 0.25, mrr: 0.6, avgLatencyMs: 42, totalEvents: 120 };

describe('RetrievalQualityCard', () => {
  beforeEach(() => { (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = { token: 't', backend: 'sqlite', version: 'x' }; });
  afterEach(() => { delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__; vi.restoreAllMocks(); });

  it('renders hit-rate, avg latency, miss rate and event count', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue(REPORT);
    render(<RetrievalQualityCard refreshKey={0} />);
    expect(await screen.findByText('75%')).toBeInTheDocument();
    expect(screen.getByText(/42\s*ms/)).toBeInTheDocument();
    expect(screen.getByText(/25%/)).toBeInTheDocument();
    expect(screen.getByText(/120 events/)).toBeInTheDocument();
  });

  it('shows an empty state when there are no events', async () => {
    vi.spyOn(api, 'tool').mockResolvedValue({ ...REPORT, totalEvents: 0 });
    render(<RetrievalQualityCard refreshKey={0} />);
    expect(await screen.findByText(/No retrieval events/i)).toBeInTheDocument();
  });

  it('shows a friendly error when the fetch fails', async () => {
    vi.spyOn(api, 'tool').mockRejectedValue(new Error('boom'));
    render(<RetrievalQualityCard refreshKey={0} />);
    expect(await screen.findByText(/load this panel/i)).toBeInTheDocument();
  });
});
