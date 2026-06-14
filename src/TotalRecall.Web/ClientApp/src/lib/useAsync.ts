import { useEffect, useRef, useState } from 'react';

export interface AsyncState<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
}

/**
 * Runs `fn` on mount and whenever any value in `deps` changes (pass a parent
 * refresh key to re-fetch). Stale resolutions are ignored via a cancelled flag.
 *
 * `loading` is true only for the FIRST load; background re-fetches (polling or
 * manual refresh) keep the previous `data` on screen and do not flip `loading`,
 * so panels refresh without flickering back to a "Loading…" state.
 *
 * @param deps Values that trigger a re-fetch when they change. Pass primitives
 *   or stable references only — never an inline array/object literal, which
 *   gets a fresh identity every render and would cause an infinite fetch loop.
 */
export function useAsync<T>(fn: () => Promise<T>, deps: unknown[]): AsyncState<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const fnRef = useRef(fn);
  fnRef.current = fn;
  const loadedOnce = useRef(false);

  useEffect(() => {
    let cancelled = false;
    if (!loadedOnce.current) setLoading(true);
    fnRef.current()
      .then((d) => { if (!cancelled) { setData(d); setError(null); loadedOnce.current = true; } })
      .catch((e) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // deps is an opaque, caller-controlled array (see the JSDoc contract above).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return { data, error, loading };
}
