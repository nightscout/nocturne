import { redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";

/**
 * Trackers always require a signed-in member: the share host renders the authenticated shell for
 * an anonymous visitor, and this page writes. Same guard as `settings/+layout.server.ts`.
 */
export const load: PageServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated || !locals.user) {
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }
};
