/**
 * Acting-As Store - Manages the "viewing data for" follower context.
 *
 * When a user has follower grants (i.e., someone has shared their data),
 * this store tracks which data owner the user is currently viewing.
 * Null means viewing own data.
 *
 * The X-Acting-As header is automatically injected into API requests
 * via the client-side API client when a target is selected.
 */

import { writable } from "svelte/store";

export interface ActingAsTarget {
  subjectId: string;
  displayName: string | null;
  email: string | null;
  scopes: string[];
  label: string | null;
}

/** Currently selected acting-as target. Null means viewing own data. */
export const actingAs = writable<ActingAsTarget | null>(null);

/** Available follower targets fetched from the API. */
export const followerTargets = writable<ActingAsTarget[]>([]);

/**
 * Returns headers to include in API requests.
 * When acting as a follower, includes the X-Acting-As header.
 *
 * This is designed for synchronous consumption outside of reactive
 * contexts (e.g., in the API client's fetch wrapper).
 */
export function getActingAsHeaders(): Record<string, string> {
  let current: ActingAsTarget | null = null;
  const unsubscribe = actingAs.subscribe((v) => (current = v));
  unsubscribe();
  if (current) {
    return { "X-Acting-As": (current as ActingAsTarget).subjectId };
  }
  return {};
}