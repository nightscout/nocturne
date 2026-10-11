import { redirect } from "@sveltejs/kit";
import { browser } from "$app/environment";
import type { LayoutLoad } from "./$types";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { isTenantlessRoute } from "$lib/navigation/tenantless-navigation";
import { isShareHost } from "$lib/share-host";

/**
 * Whether the browser still holds the session marker. The API writes it readable by script, with
 * the refresh token's expiry, and deletes it on sign-out (SessionCookieExtensions).
 */
function hasSessionCookie(): boolean {
  return document.cookie
    .split(";")
    .some((pair) => pair.trim().startsWith(`${AUTH_COOKIE_NAMES.isAuthenticated}=`));
}

/** Whether a guest link's expiry, as the server load reported it, has passed. */
function guestLinkExpired(expiresAt: string | null | undefined): boolean {
  return !!expiresAt && Date.parse(expiresAt) <= Date.now();
}

export const load: LayoutLoad = async ({ data, url, parent }) => {
  // The server load's data is reused across client navigations, so a session that has since
  // expired or been signed out elsewhere is caught here, where the server would have redirected.
  // A guest holds no session marker; its link's expiry stands in for it.
  if (browser && data.user && !isShareHost(location.hostname)) {
    const ended = data.isGuestSession
      ? guestLinkExpired(data.guestExpiresAt)
      : !hasSessionCookie();
    if (ended) {
      redirect(303, `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`);
    }
  }

  // A tenantless host resolves no tenant, so a tenant-scoped page would render its shell and
  // then 404 against the API. The nav hides those entries; this catches direct navigation and
  // stale links. It runs after the server load, so the login redirect there always pre-empts it,
  // and here rather than there so that load does not re-run for every path the guard checks.
  const { tenantless } = await parent();
  if (tenantless && !isTenantlessRoute(url.pathname)) {
    redirect(303, "/");
  }

  return data;
};
