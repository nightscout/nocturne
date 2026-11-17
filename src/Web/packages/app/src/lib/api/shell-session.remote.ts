import { query, getRequestEvent } from "$app/server";
import { error } from "@sveltejs/kit";
import { isApiOutage } from "#lib/server/api-outage.js";
import { readPermissions } from "#lib/server/resolve-permissions.js";

/**
 * The session facts the authenticated shell was rendered from, read through the same request
 * handling its server load runs behind, without that load's onboarding and tenant redirects.
 *
 * Rejects with 503 when an API outage kept any of them from being read, so that a caller never
 * mistakes the outage for a sign-out or a withdrawn grant.
 *
 * `shareLinkLive` is false on a share host whose link no longer grants anonymous read, and true
 * elsewhere.
 */
export const getShellSession = query(async () => {
  const { locals } = getRequestEvent();
  if (locals.sessionUnreadable) error(503, "Session unavailable");

  try {
    const [permissions, status] = await Promise.all([
      readPermissions(locals),
      locals.isShareHost ? readShareStatus(locals) : null,
    ]);

    return {
      subjectId: locals.isAuthenticated ? (locals.user?.subjectId ?? null) : null,
      scopes: permissions.scopes ?? [],
      limitTo24Hours: permissions.limitTo24Hours ?? false,
      isGuestSession: locals.isGuestSession ?? false,
      guestExpiresAt: locals.guestExpiresAt ?? null,
      shareLinkLive: status === null ? true : status.anonymousReadAccess === true,
    };
  } catch (e) {
    if (isApiOutage(e)) error(503, "Session unavailable");
    throw e;
  }
});

async function readShareStatus(locals: App.Locals) {
  try {
    return await locals.apiClient.status.getStatus();
  } catch (e) {
    if (isApiOutage(e)) throw e;
    return { anonymousReadAccess: false };
  }
}
