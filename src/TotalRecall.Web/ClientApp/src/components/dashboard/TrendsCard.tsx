import { Card } from '../Card';

export function TrendsCard({ refreshKey }: { refreshKey: number }) {
  void refreshKey;
  return <Card title="Trends"><p className="tr-card-muted">Loading…</p></Card>;
}
