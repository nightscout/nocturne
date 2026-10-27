import { beforeNavigate, goto, pushState } from "$app/navigation";
import { page } from "$app/state";
import type { CoachRouter } from "@nocturne/coach";
import { randomUUID } from "#lib/utils/index.js";

/** Call once per layout instance, during component init. */
export function createCoachRouter(): CoachRouter {
  // A reload mints a new key, so an entry its predecessor left marked does not read as this tour's.
  const key = `coachMark:${randomUUID()}`;
  return {
    beforeNavigate,
    goto,
    pushMarkedEntry: () => pushState("", { ...page.state, [key]: true }),
    isMarkedEntry: () => page.state[key] === true,
  };
}
