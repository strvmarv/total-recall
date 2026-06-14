import { describe, expect, it } from 'vitest';
import { movementsByDay, transitionCounts } from './trendsMath';
import type { CompactionMovement } from './types';

const mv = (ts: number, from: string, to: string | null): CompactionMovement => ({
  id: String(ts), timestamp: ts, session_id: null, source_tier: from, target_tier: to,
  source_entry_ids: [], target_entry_id: null, reason: 'decay',
});

describe('trendsMath', () => {
  it('movementsByDay counts movements per UTC day, ascending', () => {
    const d1 = Date.UTC(2026, 5, 10, 1);
    const d1b = Date.UTC(2026, 5, 10, 23);
    const d2 = Date.UTC(2026, 5, 11, 5);
    const out = movementsByDay([mv(d2, 'hot', 'warm'), mv(d1, 'hot', 'warm'), mv(d1b, 'warm', 'cold')]);
    expect(out).toEqual([{ day: '2026-06-10', count: 2 }, { day: '2026-06-11', count: 1 }]);
  });

  it('transitionCounts tallies source→target pairs, descending', () => {
    const t = Date.UTC(2026, 5, 10);
    const out = transitionCounts([mv(t, 'hot', 'warm'), mv(t, 'hot', 'warm'), mv(t, 'warm', 'cold')]);
    expect(out[0]).toEqual({ label: 'hot → warm', count: 2 });
    expect(out[1]).toEqual({ label: 'warm → cold', count: 1 });
  });

  it('transitionCounts renders a null target as ∅', () => {
    const t = Date.UTC(2026, 5, 10);
    expect(transitionCounts([mv(t, 'hot', null)])[0]).toEqual({ label: 'hot → ∅', count: 1 });
  });
});
