import { useState } from 'react';
import { api } from '../../lib/api';
import type { KbIngestDirResult, KbIngestFileResult } from '../../lib/types';

export function KbIngest({ onIngested }: { onIngested: () => void }) {
  const [filePath, setFilePath] = useState('');
  const [dirPath, setDirPath] = useState('');
  const [glob, setGlob] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function ingestFile(e: React.FormEvent) {
    e.preventDefault();
    if (!filePath.trim()) return;
    setBusy(true); setError(null); setMessage(null);
    try {
      const r = await api.tool<KbIngestFileResult>('kb_ingest_file', { path: filePath.trim() });
      setMessage(`Ingested ${r.chunk_count} chunks.`);
      setFilePath('');
      onIngested();
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  async function ingestDir(e: React.FormEvent) {
    e.preventDefault();
    if (!dirPath.trim()) return;
    setBusy(true); setError(null); setMessage(null);
    try {
      const r = await api.tool<KbIngestDirResult>('kb_ingest_dir', { path: dirPath.trim(), glob: glob.trim() || undefined });
      const errSuffix = r.errors.length > 0 ? ` (${r.errors.length} file(s) failed)` : '';
      setMessage(`Ingested ${r.document_count} documents (${r.total_chunks} chunks)${errSuffix}.`);
      setDirPath(''); setGlob('');
      onIngested();
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  return (
    <div className="tr-kb-panel">
      <form className="tr-kb-form" onSubmit={ingestFile}>
        <div className="tr-field" style={{ flex: 1 }}>
          <label htmlFor="tr-kb-file">File path</label>
          <input id="tr-kb-file" className="tr-input" value={filePath} onChange={(e) => setFilePath(e.target.value)} placeholder="/path/to/file.md" />
        </div>
        <button type="submit" className="tr-btn" disabled={busy || !filePath.trim()}>Ingest file</button>
      </form>
      <form className="tr-kb-form" onSubmit={ingestDir} style={{ marginTop: 'var(--tr-space-3)' }}>
        <div className="tr-field" style={{ flex: 1 }}>
          <label htmlFor="tr-kb-dir">Directory path</label>
          <input id="tr-kb-dir" className="tr-input" value={dirPath} onChange={(e) => setDirPath(e.target.value)} placeholder="/path/to/docs" />
        </div>
        <div className="tr-field">
          <label htmlFor="tr-kb-glob">Glob (optional)</label>
          <input id="tr-kb-glob" className="tr-input" style={{ minWidth: 140 }} value={glob} onChange={(e) => setGlob(e.target.value)} placeholder="**/*.md" />
        </div>
        <button type="submit" className="tr-btn" disabled={busy || !dirPath.trim()}>Ingest directory</button>
      </form>
      {message && <p className="tr-kb-result" role="status">{message}</p>}
      {error && <p className="tr-card-error tr-kb-result" role="alert">{error}</p>}
    </div>
  );
}
