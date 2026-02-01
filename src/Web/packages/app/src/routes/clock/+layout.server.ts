import type { LayoutServerLoad } from "./$types";
import { redirect } from "@sveltejs/kit";

/**
 * Server-side layout load function for clock routes
 * The /clock list page requires auth, but /clock/[id] display does not
 */
export const load: LayoutServerLoad = async ({ locals, url }) => {
  // /clock/[id] routes are public (for sharing clock displays)
  // Only the list page (/clock) requires authentication
  const isDisplayRoute = /^\/clock\/[^/]+$/.test(url.pathname);

  if (!isDisplayRoute) {
    // Check if user is authenticated for the list page
    if (!locals.isAuthenticated || !locals.user) {
      const returnUrl = encodeURIComponent(url.pathname + url.search);
      throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
    }
  }

  return {
    user: locals.user ?? null,
  };
};
