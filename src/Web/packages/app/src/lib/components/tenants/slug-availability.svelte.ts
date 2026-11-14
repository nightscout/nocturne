import { Debounced } from "runed";
import type { SlugValidationResult } from "#lib/api/index.js";

/** The parts of a remote query a slug check reads. */
interface SlugQuery {
  readonly current: SlugValidationResult | undefined;
  readonly loading: boolean;
  readonly error: unknown;
}

export type SlugAvailability =
  | { state: "empty" }
  | { state: "checking" }
  | { state: "available"; slug: string }
  | { state: "refused"; message: string };

/**
 * Asks the API whether the subdomain being typed is free, 400 ms after the last keystroke. The
 * API is the only judge of a slug: this sends what was typed, lower-cased and trimmed, and shows
 * its answer.
 */
export function createSlugAvailability(
  input: () => string,
  check: (slug: string) => SlugQuery,
) {
  const normalized = $derived(input().trim().toLowerCase());
  const debounced = new Debounced(() => normalized, 400);
  const query = $derived(debounced.current ? check(debounced.current) : null);

  const availability = $derived.by((): SlugAvailability => {
    if (!normalized) return { state: "empty" };
    if (debounced.current !== normalized || !query || query.loading || !query.current) {
      if (query?.error) return { state: "refused", message: "Could not check the address. Try again." };
      return { state: "checking" };
    }
    return query.current.isValid
      ? { state: "available", slug: normalized }
      : { state: "refused", message: query.current.message ?? "You cannot use this address." };
  });

  return {
    get normalized() {
      return normalized;
    },
    get current() {
      return availability;
    },
  };
}
