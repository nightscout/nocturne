import { redirect } from "@sveltejs/kit";
import { signInAddress } from "#lib/navigation/sign-in-address.js";
import type { LayoutServerLoad } from "./$types";

/**
 * Community requires a signed-in member, as the translation editor saves drafts to the account.
 * Same guard as `settings/+layout.server.ts`.
 */
export const load: LayoutServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated || !locals.user) {
    throw redirect(303, signInAddress(url.pathname + url.search));
  }
};
