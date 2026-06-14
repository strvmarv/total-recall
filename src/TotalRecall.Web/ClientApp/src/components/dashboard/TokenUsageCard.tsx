import { Card } from '../Card';

export function TokenUsageCard({ refreshKey }: { refreshKey: number }) {
  void refreshKey;
  return <Card title="Token usage" drillTo="/usage"><p className="tr-card-muted">Loading…</p></Card>;
}
