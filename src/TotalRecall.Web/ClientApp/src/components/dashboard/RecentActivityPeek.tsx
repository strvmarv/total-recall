import { Card } from '../Card';

export function RecentActivityPeek({ refreshKey }: { refreshKey: number }) {
  void refreshKey;
  return <Card title="🕒 Recent activity" drillTo="/memory"><p className="tr-card-muted">Loading…</p></Card>;
}
