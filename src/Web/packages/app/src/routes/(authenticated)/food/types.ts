import type { Food } from '$api';

/** GI levels as string identifiers matching the backend's integer enum */
export type GiLevel = 'low' | 'medium' | 'high';

/** Map backend GI integer to string level */
export function giFromInt(gi: number | undefined): GiLevel {
  if (gi === 1) return 'low';
  if (gi === 3) return 'high';
  return 'medium';
}

/** Map string level back to backend integer */
export function giToInt(level: GiLevel): number {
  if (level === 'low') return 1;
  if (level === 'high') return 3;
  return 2;
}

export type SortMode = 'name' | 'carbs' | 'recent';

export type { Food };
