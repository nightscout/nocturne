import { readable } from "svelte/store";
import { page as state } from "$app/state";

/**
 * `$app/stores` for the feature bundles: `sveltekit-search-params`, which some feature pages read
 * their filters through, still subscribes to it. It reads the same stand-in page as `$app/state`.
 */
export const page = readable(state);
export const navigating = readable(null);
export const updated = {
  ...readable(false),
  check: () => Promise.resolve(false),
};
export const getStores = () => ({ page, navigating, updated });
