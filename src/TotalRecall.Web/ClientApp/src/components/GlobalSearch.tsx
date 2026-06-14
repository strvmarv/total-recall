import { useState } from 'react';
import { useNavigate } from 'react-router-dom';

export function GlobalSearch() {
  const [q, setQ] = useState('');
  const navigate = useNavigate();

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const term = q.trim();
    if (term) navigate(`/memory?q=${encodeURIComponent(term)}`);
  }

  return (
    <form className="tr-search" role="search" onSubmit={onSubmit}>
      <input
        type="search"
        aria-label="Search memory"
        placeholder="Search memory…"
        value={q}
        onChange={(e) => setQ(e.target.value)}
      />
    </form>
  );
}
