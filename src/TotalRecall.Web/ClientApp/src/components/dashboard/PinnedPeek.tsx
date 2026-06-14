import { Card } from '../Card';

export function PinnedPeek({ refreshKey }: { refreshKey: number }) {
  void refreshKey;
  return <Card title="📌 Pinned directives" drillTo="/memory"><p className="tr-card-muted">Loading…</p></Card>;
}
