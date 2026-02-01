import type { LayoutServerLoad } from "./$types";
import { redirect } from "@sveltejs/kit";

/**
 * Server-side layout load function for protected routes
 * Redirects unauthenticated users to the login page
 */
export const load: LayoutServerLoad = async ({ locals, url }) => {
  // Check if user is authenticated
  if (!locals.isAuthenticated || !locals.user) {
    // Redirect to login with return URL
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }

  // Return user info for use in the layout and child routes
  return {
    user: locals.user,
  };
};
