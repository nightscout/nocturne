import type { RequestHandler } from "./$types";
import { redirect } from "@sveltejs/kit";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { logout } from "$lib/data/generated";

/**
 * Helper to clear all auth cookies
 */
function clearAuthCookies(cookies: Parameters<RequestHandler>[0]["cookies"]) {
  cookies.delete(AUTH_COOKIE_NAMES.accessToken, { path: "/" });
  cookies.delete(AUTH_COOKIE_NAMES.refreshToken, { path: "/" });
  cookies.delete("IsAuthenticated", { path: "/" });
}

/**
 * POST handler for logout
 * Calls the API to revoke the session and clears cookies
 */
export const POST: RequestHandler = async ({ cookies }) => {
  try {
    const result = await logout(undefined);

    // Clear all auth cookies
    clearAuthCookies(cookies);

    // If provider has a logout URL, redirect there
    if (result?.providerLogoutUrl) {
      throw redirect(303, result.providerLogoutUrl);
    }
  } catch (error) {
    // If it's a redirect, re-throw it
    if (error instanceof Response) {
      throw error;
    }

    console.error("Logout error:", error);

    // Clear cookies on error too
    clearAuthCookies(cookies);
  }

  // Redirect to home page
  throw redirect(303, "/");
};

/**
 * GET handler for logout (for direct link navigation)
 * Shows a confirmation page or redirects immediately based on preference
 */
export const GET: RequestHandler = async ({ cookies }) => {
  // For GET requests, redirect to a logout confirmation page
  // or perform the logout immediately based on app settings
  // For now, we'll redirect to the login page after clearing cookies

  try {
    await logout(undefined);
  } catch (error) {
    console.error("Logout error:", error);
  }

  // Clear cookies
  clearAuthCookies(cookies);

  // Redirect to login page
  throw redirect(303, "/auth/login");
};
