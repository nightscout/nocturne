import type { RequestHandler } from "./$types";
import { redirect } from "@sveltejs/kit";
import { clearAuthCookies } from "#lib/config/auth-cookies.js";
import { logout } from "#lib/api/generated/oidcs.generated.remote.js";

/**
 * Logout is POST-only: a GET that revokes the session is reachable from any
 * third-party page (`<img src=".../auth/logout">`) and from link prefetchers.
 * SvelteKit's CSRF origin check covers form POSTs, so callers submit a form.
 */
export const POST: RequestHandler = async ({ cookies }) => {
  let providerLogoutUrl: string | undefined;

  try {
    providerLogoutUrl = (await logout(undefined))?.providerLogoutUrl ?? undefined;
  } catch (error) {
    console.error("Logout error:", error);
  }

  // Clear cookies whether or not the backend revocation succeeded.
  clearAuthCookies(cookies);

  throw redirect(303, providerLogoutUrl ?? "/", { external: true });
};
