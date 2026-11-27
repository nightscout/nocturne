import { redirect } from "@sveltejs/kit";
import { signInAddress } from "#lib/navigation/sign-in-address.js";
import type { LayoutServerLoad } from "./$types";

/**
 * Settings always require authentication, even when the site is public.
 * Without this guard, unauthenticated visitors see a flash of the settings
 * page before a client-side API failure triggers the OAuth redirect.
 */
export const load: LayoutServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated || !locals.user) {
    throw redirect(303, signInAddress(url.pathname + url.search));
  }
};
