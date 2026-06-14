import { useCallback, useEffect, useState } from 'react';
import { TierCompositionCard } from '../components/dashboard/TierCompositionCard';
import { TokenUsageCard } from '../components/dashboard/TokenUsageCard';
import { RetrievalQualityCard } from '../components/dashboard/RetrievalQualityCard';
import { TrendsCard } from '../components/dashboard/TrendsCard';
import { PinnedPeek } from '../components/dashboard/PinnedPeek';
import { RecentActivityPeek } from '../components/dashboard/RecentActivityPeek';

// Single-user local app: 15s keeps panels fresh without hammering the DB.
// (Polling interval becomes Config-tunable in a later plan.)
const POLL_MS = 15000;

export function Dashboard() {
  const [refreshKey, setRefreshKey] = useState(0);
  const refresh = useCallback(() => setRefreshKey((k) => k + 1), []);

  useEffect(() => {
    const id = setInterval(refresh, POLL_MS);
    return () => clearInterval(id);
  }, [refresh]);

  return (
    <section className="tr-dashboard">
      <header className="tr-dashboard-head">
        <h1>Dashboard</h1>
        <button type="button" className="tr-refresh" onClick={refresh} aria-label="Refresh dashboard">↻ Refresh</button>
      </header>
      <div className="tr-card-grid">
        <TierCompositionCard refreshKey={refreshKey} />
        <TokenUsageCard refreshKey={refreshKey} />
        <RetrievalQualityCard refreshKey={refreshKey} />
        <TrendsCard refreshKey={refreshKey} />
      </div>
      <div className="tr-peek-grid">
        <PinnedPeek refreshKey={refreshKey} />
        <RecentActivityPeek refreshKey={refreshKey} />
      </div>
    </section>
  );
}
