import type { LayoutServerLoad } from "./$types";
import { invalidateOnboardingCache } from "#lib/server/onboarding-check.js";

/**
 * Setup wizard layout server load.
 * Clears the onboarding cookie on entry so that when the user navigates
 * away from the setup wizard, the onboarding check re-evaluates from scratch.
 */
export const load: LayoutServerLoad = async ({ cookies }) => {
  invalidateOnboardingCache(cookies);
  return {};
};
