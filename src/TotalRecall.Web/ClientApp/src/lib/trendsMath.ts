import type { CompactionMovement } from './types';

function dayKey(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10); // UTC YYYY-MM-DD
}

export interface DayCount { day: string; count: number; }
export function movementsByDay(movements: CompactionMovement[]): DayCount[] {
  const m = new Map<string, number>();
  for (const mv of movements) {
    const k = dayKey(mv.timestamp);
    m.set(k, (m.get(k) ?? 0) + 1);
  }
  return [...m.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([day, count]) => ({ day, count }));
}

export interface Transition { label: string; count: number; }
export function transitionCounts(movements: CompactionMovement[]): Transition[] {
  const m = new Map<string, number>();
  for (const mv of movements) {
    const label = `${mv.source_tier} → ${mv.target_tier ?? '∅'}`;
    m.set(label, (m.get(label) ?? 0) + 1);
  }
  return [...m.entries()].sort((a, b) => b[1] - a[1]).map(([label, count]) => ({ label, count }));
}
