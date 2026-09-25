import { json } from "@sveltejs/kit";
import type { RequestHandler } from "./$types";
import { env } from "$env/dynamic/private";
import {
  REALTIME_ADMISSION_PATH,
  signHandshakeTicket,
} from "@nocturne/bridge/ticket";
import type { RealtimeAdmission } from "$lib/api";
import {
  getApiBaseUrl,
  createServerHttpClient,
} from "$lib/server/api-client-factory";
import { getEffectiveHost, getOriginalProto } from "$lib/server/request-host";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";

/**
 * Mints a short-lived ticket that authorizes a Socket.IO handshake to the
 * realtime bridge for the tenant this request's host resolves to.
 *
 * The browser's Socket.IO handshake reaches the bridge directly, outside this
 * BFF, so it can't refresh the 15-minute access token the way a proxied `/api`
 * call does. Instead, this endpoint (which IS inside the BFF) asks the API for
 * the connection's realtime admission and, only on a 2xx, signs a ticket the
 * bridge verifies locally. The admission is gated on the same glucose read as
 * the API's own feed, and says whether the credential may join the tenant-wide
 * room; the ticket carries that answer so the bridge needs no per-connection
 * API call.
 *
 * Always responds 200 with `{ token: string | null, retry?: boolean }`. A null
 * token means no ticket was minted; `retry: true` distinguishes a transient
 * failure (API unreachable / 5xx, or this instance misconfigured — the client
 * should keep trying) from a definitive denial (`retry` absent — the user isn't
 * permitted realtime, so the client stays quietly disconnected rather than
 * surfacing a connection error).
 */
export const GET: RequestHandler = async (event) => {
  const secret = env.INSTANCE_KEY;
  const apiBaseUrl = getApiBaseUrl();
  const effectiveHost = getEffectiveHost(event.request, event.cookies);

  // Fail closed: without the signing secret, the API URL, or a resolvable host
  // we cannot mint a trustworthy ticket. This is a deployment fault, not a
  // per-user denial, so it is transient: a definitive denial would latch the
  // client into a terminal "realtime not permitted" state that no longer
  // recovers once the operator fixes the configuration.
  if (!secret || !apiBaseUrl || !effectiveHost) {
    return json({ token: null, retry: true });
  }

  // Carries only the caller's own cookies. The instance key would authenticate
  // a visitor with no cookie as the instance service and admit them to any
  // tenant's room. Token rotation still flows back to the browser.
  const httpClient = createServerHttpClient(event.fetch, {
    accessToken: event.cookies.get(AUTH_COOKIE_NAMES.accessToken),
    refreshToken: event.cookies.get(AUTH_COOKIE_NAMES.refreshToken),
    guestSessionToken: event.cookies.get(AUTH_COOKIE_NAMES.guestSession),
    platformAccessToken: event.cookies.get(AUTH_COOKIE_NAMES.platformAccess),
    extraHeaders: {
      "X-Forwarded-Host": effectiveHost,
      "X-Forwarded-Proto": getOriginalProto(event.request),
      // Bypass the API response cache: it runs before auth and keys on the
      // constant internal Host, so a cached authed 200 could otherwise authorize
      // an unauthenticated probe. ('no-store' alone does not bypass the lookup.)
      "Cache-Control": "no-cache, no-store",
    },
    responseCookies: event.cookies,
    rawSetCookies: event.locals.rawSetCookies,
    signal: event.request.signal,
  });

  // Bound the probe (5s): the client fetches this inside the Socket.IO `auth`
  // callback, which has no timeout of its own, so a hung API must not wedge the
  // handshake. The request's own abort signal is already bound via the client.
  let probeStatus: number;
  try {
    const probe = await httpClient.fetch(
      `${apiBaseUrl}${REALTIME_ADMISSION_PATH}`,
      { method: "GET", signal: AbortSignal.timeout(5000) }
    );
    if (probe.ok) {
      const admission: Partial<RealtimeAdmission> = await probe.json();
      return json({
        token: signHandshakeTicket(
          secret,
          effectiveHost,
          admission.tenantRelay === true,
          admission.subjectId ?? undefined
        ),
      });
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
