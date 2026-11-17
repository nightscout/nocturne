import type { RequestEvent } from "@sveltejs/kit";
import { createServerHttpClient } from "#lib/server/api-client-factory.js";
import { clientAddressHeaders } from "#lib/server/client-address.js";
import { getEffectiveHost, getOriginalProto } from "#lib/server/request-host.js";
import {
  AUTO_LOGIN_MARKER,
  DEMO_LOGIN_ENDPOINT,
  autoLoginEndpoint,
} from "#lib/server/auto-login.js";
import { isRecord, nonEmptyString } from "#lib/utils/type-guards.js";

/** Tokens of a session minted during this request, which the browser has not yet sent back. */
export interface MintedSession {
  accessToken: string;
  refreshToken: string;
}

const AUTHENTICATED_ROUTES = "/(authenticated)";

/**
 * Signs an anonymous visitor to a demo tenant's app in as its demo member within this request,
 * so the page renders signed in instead of redirecting through the demo session endpoint and
 * back. Null when the request is not eligible or the mint fails; the authenticated layout then
 * falls back to that redirect, once, behind the auto-login marker.
 *
 * The call carries no instance key: a demo session is what any visitor may ask for. It does carry
 * the visitor's signed address, or the endpoint's rate limit would be one bucket for every visitor.
 */
export async function mintDemoSession(
  event: Pick<
    RequestEvent,
    "request" | "cookies" | "locals" | "url" | "route" | "getClientAddress"
  >,
  apiBaseUrl: string,
  fetchFn: typeof fetch
): Promise<MintedSession | null> {
  if (event.request.method !== "GET") return null;
  if (!event.route.id?.startsWith(AUTHENTICATED_ROUTES)) return null;
  if (event.url.searchParams.has(AUTO_LOGIN_MARKER)) return null;

  const status = await event.locals.statusPromise;
  if (autoLoginEndpoint(status?.isDemo, event.locals.isShareHost) !== DEMO_LOGIN_ENDPOINT) {
    return null;
  }

  const extraHeaders: Record<string, string> = {
    "X-Forwarded-Proto": getOriginalProto(event.request),
    ...clientAddressHeaders(event),
  };
  const forwardedHost = getEffectiveHost(event.request, event.cookies);
  if (forwardedHost) extraHeaders["X-Forwarded-Host"] = forwardedHost;

  try {
    const rawSink = event.locals.rawSetCookies;
    const response = await createServerHttpClient(fetchFn, {
      extraHeaders,
      responseCookies: event.cookies,
      rawSetCookies: rawSink,
    }).fetch(new URL(`${DEMO_LOGIN_ENDPOINT}?format=json`, apiBaseUrl).toString(), {
      redirect: "manual",
    });
    if (!response.ok) return null;

    const body: unknown = await response.json();
    if (!isRecord(body)) return null;
    const accessToken = nonEmptyString(body.accessToken);
    const refreshToken = nonEmptyString(body.refreshToken);
    return accessToken && refreshToken ? { accessToken, refreshToken } : null;
  } catch (error) {
    console.error("Failed to mint a demo session:", error);
    return null;
  }
}
