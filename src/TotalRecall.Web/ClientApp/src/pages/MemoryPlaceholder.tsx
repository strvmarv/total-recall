import { useSearchParams } from 'react-router-dom';
import { SectionPlaceholder } from './SectionPlaceholder';

export function MemoryPlaceholder() {
  const [params] = useSearchParams();
  const q = params.get('q');
  return <SectionPlaceholder title="Memory" note={q ? `Search: "${q}"` : undefined} />;
}
