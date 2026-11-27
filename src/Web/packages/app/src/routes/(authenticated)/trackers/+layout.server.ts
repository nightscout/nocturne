import { redirect } from "@sveltejs/kit";
import { signInAddress } from "#lib/navigation/sign-in-address.js";
import type { LayoutServerLoad } from "./$types";

/**
 * Trackers always require a signed-in member: the share host renders the authenticated shell for
 * an anonymous visitor, and its pages write. Same guard as `settings/+layout.server.ts`.
 */
export const load: LayoutServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated || !locals.user) {
    throw redirect(303, signInAddress(url.pathname + url.search));
  }
};
