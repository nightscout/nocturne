import { json } from "@sveltejs/kit";
import type { RequestHandler } from "./$types";
import { env } from "$env/dynamic/private";
import { signHandshakeTicket } from "@nocturne/bridge/ticket";
import {
  getApiBaseUrl,
  getHashedInstanceKey,
  createServerHttpClient,
} from "$lib/server/api-client-factory";
import { getEffectiveHost, getOriginalProto } from "$lib/server/request-host";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";

/**
 * Mints a short-lived ticket that authorizes a Socket.IO handshake to the
 * realtime bridge for the tenant this request's host resolves to.
 *
 * The browser's Socket.IO handshake reaches the bridge directly, outside this
 * BFF — so it can't refresh the 15-minute access token or attach the service
 * instance key the way a proxied `/api` call does. Instead, this endpoint (which
 * IS inside the BFF) replays the connection's read against the API's per-tenant
 * read policy and, only on a 2xx, signs a ticket the bridge verifies locally.
 * This mirrors the read policy exactly — authenticated members and public-read
 * tenants pass; private/cross-tenant connections do not — without the bridge
 * making a per-connection API call.
 *
 * Always responds 200 with `{ token: string | null, retry?: boolean }`. A null
 * token means no ticket was minted; `retry: true` distinguishes a transient
 * failure (API unreachable / 5xx — the client should keep trying) from a
 * definitive denial (`retry` absent — the user isn't permitted realtime, so the
 * client stays quietly disconnected rather than surfacing a connection error).
 */
export const GET: RequestHandler = async (event) => {
  const secret = env.INSTANCE_KEY;
  const apiBaseUrl = getApiBaseUrl();
  const effectiveHost = getEffectiveHost(event.request, event.cookies);

  // Fail closed: without the signing secret, the API URL, or a resolvable host
  // we cannot mint a trustworthy ticket.
  if (!secret || !apiBaseUrl || !effectiveHost) {
    return json({ token: null });
  }

  // Probe the read endpoint from inside the BFF so the instance key is attached,
  // session cookies are forwarded, and any token rotation flows back to the
  // browser as Set-Cookie.
  const httpClient = createServerHttpClient(event.fetch, {
    accessToken: event.cookies.get(AUTH_COOKIE_NAMES.accessToken),
    refreshToken: event.cookies.get(AUTH_COOKIE_NAMES.refreshToken),
    guestSessionToken: event.cookies.get("nocturne-guest-session"),
    platformAccessToken: event.cookies.get(AUTH_COOKIE_NAMES.platformAccess),
    hashedInstanceKey: getHashedInstanceKey(),
    extraHeaders: {
      "X-Forwarded-Host": effectiveHost,
      "X-Forwarded-Proto": getOriginalProto(event.request),
      // Bypass the API response cache: it runs before auth and keys on the
      // constant internal Host, so a cached authed 200 could otherwise authorize
      // an unauthenticated probe. ('no-store' alone does not bypass the lookup.)
      "Cache-Control": "no-cache, no-store",
    },
    responseCookies: event.cookies,
    signal: event.request.signal,
  });

  // Bound the probe (5s): the client fetches this inside the Socket.IO `auth`
  // callback, which has no timeout of its own, so a hung API must not wedge the
  // handshake. The request's own abort signal is already bound via the client.
  let probeStatus: number | null = null;
  try {
    const probe = await httpClient.fetch(
      `${apiBaseUrl}/api/v1/entries?count=1`,
      { method: "GET", signal: AbortSignal.timeout(5000) },
    );
    if (probe.ok) {
      return json({ token: signHandshakeTicket(secret, effectiveHost) });
    }
    probeStatus = probe.status;
  } catch {
    // Network failure or timeout — transient; tell the client to keep retrying.
    return json({ token: null, retry: true });
  }

  // 401/403 are definitive denials (not a member, tenant not public). Any other
  // status (e.g. 5xx) is transient, so the client should keep retrying rather
  // than going permanently quiet.
  const transient = probeStatus !== 401 && probeStatus !== 403;
  return json({ token: null, retry: transient });
};
