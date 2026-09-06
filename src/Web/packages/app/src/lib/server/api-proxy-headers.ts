import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import {
  CLIENT_IP_HEADER,
  CLIENT_IP_SIGNATURE_HEADER,
} from "$lib/server/client-address";

/** The subset of SvelteKit's Cookies the proxy reads. */
export interface ProxyCookieReader {
  get(name: string): string | undefined;
}

export interface ProxyHeaderInput {
  /** Headers of the incoming browser request, forwarded onward as-is apart from the below. */
  requestHeaders: Headers;
  /** Host the API should resolve the tenant from, or null to leave the header off. */
  effectiveHost: string | null | undefined;
  /** Scheme the browser used, for the API's URL building behind the proxy. */
  proto: string;
  /** Whether this request arrived on a public share host. */
  isShareHost: boolean;
  cookies: ProxyCookieReader;
}

/**
 * Headers for one proxied /api request.
 *
 * This proxy carries end-user browser calls, so it forwards only the user's own credentials and
 * never the instance key — attaching that would authenticate anonymous visitors as admin and
 * bypass per-tenant public access. Any client-supplied instance headers are stripped for the
 * same reason.
 *
 * On a share host it forwards no credentials at all (see authHandle). Here the incoming Cookie
 * header has to be deleted rather than merely left unread, since it is passed through onward
 * as-is.
 */
export function buildProxyHeaders({
  requestHeaders,
  effectiveHost,
  proto,
  isShareHost,
  cookies,
}: ProxyHeaderInput): Headers {
  const headers = new Headers(requestHeaders);

  if (effectiveHost) {
    headers.set("X-Forwarded-Host", effectiveHost);
  }
  headers.set("X-Forwarded-Proto", proto);

  headers.delete("X-Instance-Key");
  headers.delete("X-Instance-Service");

  // On this path the address the API partitions on is the X-Forwarded-For entry the gateway left,
  // forwarded on with the rest of the incoming headers. A client-written pair is unsigned and so
  // ignored, but it has no business travelling either.
  headers.delete(CLIENT_IP_HEADER);
  headers.delete(CLIENT_IP_SIGNATURE_HEADER);

  if (isShareHost) {
    headers.delete("Cookie");
    return headers;
  }

  const forwarded = [
    AUTH_COOKIE_NAMES.accessToken,
    AUTH_COOKIE_NAMES.refreshToken,
    AUTH_COOKIE_NAMES.guestSession,
    AUTH_COOKIE_NAMES.platformAccess,
    AUTH_COOKIE_NAMES.recoverySession,
  ]
    .map((name) => {
      const value = cookies.get(name);
      return value ? `${name}=${value}` : null;
    })
    .filter((pair) => pair !== null);

  if (forwarded.length > 0) {
    headers.set("Cookie", forwarded.join("; "));
  }

  return headers;
}
