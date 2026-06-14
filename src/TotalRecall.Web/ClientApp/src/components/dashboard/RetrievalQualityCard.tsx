import { Card } from '../Card';

export function RetrievalQualityCard({ refreshKey }: { refreshKey: number }) {
  void refreshKey;
  return <Card title="Retrieval quality"><p className="tr-card-muted">Loading…</p></Card>;
}
