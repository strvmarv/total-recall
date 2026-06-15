import { useCallback, useState } from 'react';

export type Theme = 'dark' | 'light';
const KEY = 'tr-theme';

function current(): Theme {
  return document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
}

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(current);

  const apply = useCallback((next: Theme) => {
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem(KEY, next); } catch { /* ignore */ }
    setTheme(next);
  }, []);

  const toggle = useCallback(() => {
    apply(current() === 'dark' ? 'light' : 'dark');
  }, [apply]);

  return { theme, toggle, setTheme: apply };
}
