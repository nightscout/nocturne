import { PersistedState } from "runed";

const MAX_RECENTS = 5;

export const pinnedItemIds = new PersistedState<string[]>(
  "nocturne-command-palette-pins",
  []
);

export const recentItemIds = new PersistedState<string[]>(
  "nocturne-command-palette-recents",
  []
);

export function togglePin(itemId: string): void {
  const current = pinnedItemIds.current;
  if (current.includes(itemId)) {
    pinnedItemIds.current = current.filter((id) => id !== itemId);
  } else {
    pinnedItemIds.current = [...current, itemId];
  }
}

export function isPinned(itemId: string): boolean {
  return pinnedItemIds.current.includes(itemId);
}

export function recordRecent(itemId: string): void {
  const current = recentItemIds.current.filter((id) => id !== itemId);
  recentItemIds.current = [itemId, ...current].slice(0, MAX_RECENTS);
}
