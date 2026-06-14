export function SectionPlaceholder({ title, note }: { title: string; note?: string }) {
  return (
    <section className="tr-placeholder">
      <h1>{title}</h1>
      {note && <p className="tr-placeholder-note">{note}</p>}
      <p className="tr-placeholder-sub">This section arrives in a later plan.</p>
    </section>
  );
}
