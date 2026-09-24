import type { RequestEvent } from "@sveltejs/kit";
import { createServerApiClient } from "$lib/server/api-client-factory";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { getEffectiveHost, getOriginalProto } from "$lib/server/request-host";

/**
 * Signs the request in as a guest when the API accepts its guest-session
 * cookie. Asks with that cookie alone: with the instance key attached, a cookie
 * the API cannot read would authenticate as the instance service instead, and
 * every visitor would render as a signed-in guest.
 */
export async function authenticateGuestSession(
  event: Pick<RequestEvent, "request" | "cookies" | "locals">,
  apiBaseUrl: string,
  fetchFn: typeof fetch
): Promise<void> {
  const guestSessionToken = event.cookies.get(AUTH_COOKIE_NAMES.guestSession);
  if (!guestSessionToken) return;

  const extraHeaders: Record<string, string> = {
    "X-Forwarded-Proto": getOriginalProto(event.request),
  };
  const forwardedHost = getEffectiveHost(event.request, event.cookies);
  if (forwardedHost) extraHeaders["X-Forwarded-Host"] = forwardedHost;

  try {
    const session = await createServerApiClient(apiBaseUrl, fetchFn, {
      guestSessionToken,
      extraHeaders,
    }).oidc.getSession();

    if (session?.isAuthenticated) {
      event.locals.user = {
        subjectId: session.subjectId ?? "guest",
        name: "Guest",
        email: undefined,
        roles: [],
        permissions: session.permissions ?? [],
        expiresAt: session.expiresAt,
      };
      event.locals.isAuthenticated = true;
      event.locals.isGuestSession = true;
      event.locals.guestExpiresAt = session.expiresAt;
    }
  } catch (error) {
    console.error("Failed to validate guest session:", error);
  }
}
