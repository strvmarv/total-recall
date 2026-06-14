import { Card } from '../Card';

export function TierCompositionCard({ refreshKey }: { refreshKey: number }) {
  void refreshKey;
  return <Card title="Tier composition"><p className="tr-card-muted">Loading…</p></Card>;
}
