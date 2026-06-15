import { useEffect, useState } from 'react';

export interface ChartTheme {
  accent: string;
  grid: string;
  tick: string;
  tierPinned: string;
  tierHot: string;
  tierWarm: string;
  tierCold: string;
  tierKb: string;
  mono: string;
}

/** Resolve current theme tokens for Recharts props (stroke/fill/grid/tick). */
export function chartTheme(): ChartTheme {
  const s = getComputedStyle(document.documentElement);
  const v = (name: string) => s.getPropertyValue(name).trim();
  return {
    accent: v('--tr-accent') || '#ffb454',
    grid: v('--tr-border') || 'rgba(255,255,255,0.09)',
    tick: v('--tr-text-muted') || '#8b919b',
    tierPinned: v('--tr-tier-pinned'),
    tierHot: v('--tr-tier-hot'),
    tierWarm: v('--tr-tier-warm'),
    tierCold: v('--tr-tier-cold'),
    tierKb: v('--tr-tier-kb'),
    mono: v('--tr-font-mono'),
  };
}

/** Reactive variant: re-resolves tokens whenever the <html> data-theme attribute
 *  changes, so charts recolor live on theme toggle. */
export function useChartTheme(): ChartTheme {
  const [theme, setTheme] = useState<ChartTheme>(() => chartTheme());
  useEffect(() => {
    const el = document.documentElement;
    const obs = new MutationObserver(() => setTheme(chartTheme()));
    obs.observe(el, { attributes: true, attributeFilter: ['data-theme'] });
    // Re-resolve once after mount in case custom props weren't ready at first read.
    setTheme(chartTheme());
    return () => obs.disconnect();
  }, []);
  return theme;
}
