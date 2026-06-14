export function MemoryDetail({ id, onClose }: { id: string; onClose: () => void; onChanged: () => void }) {
  return (
    <aside className="tr-memory-detail" aria-label="Entry detail">
      <button type="button" onClick={onClose} aria-label="Close detail">✕</button>
      <p className="tr-card-muted">Detail for {id}</p>
    </aside>
  );
}
