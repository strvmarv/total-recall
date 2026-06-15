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
    tierPinned: v('--tr-tier-pinned') || '#a855f7',
    tierHot: v('--tr-tier-hot') || '#ff6b6b',
    tierWarm: v('--tr-tier-warm') || '#f59e0b',
    tierCold: v('--tr-tier-cold') || '#3b82f6',
    tierKb: v('--tr-tier-kb') || '#4ade80',
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
    return () => obs.disconnect();
  }, []);
  return theme;
}
