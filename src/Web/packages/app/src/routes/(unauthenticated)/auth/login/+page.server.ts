import { redirect } from "@sveltejs/kit";
import { env } from "$env/dynamic/private";
import type { PageServerLoad } from "./$types";

// Marker appended to returnUrl so a single auto-login attempt can be detected
// after it bounces back. It survives the round-trip because the auth guard
// rebuilds returnUrl from pathname + search.
const AUTO_LOGIN_MARKER = "__autologin";

// Auto-login endpoints. Both issue a real session for a tenant member, set the
// normal cookies, and bounce back to the redirect target. Every auth guard
// funnels to /auth/login?returnUrl=..., so this one hook covers all of them.
//
// Dev auto-login: with NOCTURNE_DEV_AUTO_LOGIN=true (forwarded from the host
// environment by the Aspire AppHost, run mode only), sign in as the tenant's
// first owner instead of rendering the passkey UI. The endpoint exists only when
// the API runs in Development; elsewhere the redirect lands on a 404, never a
// session.
//
// Demo sign-in: a demo tenant has no owner credentials and exists to be explored,
// so sign every visitor in as its shared demo member. The endpoint responds only
// on a tenant whose IsDemo flag is set; any other tenant gets a 404.
const DEV_LOGIN_ENDPOINT = "/api/v4/dev-only/auth/login";
const DEMO_LOGIN_ENDPOINT = "/api/v4/demo/session";

export const load: PageServerLoad = async ({ url, locals }) => {
  const endpoint = await resolveAutoLoginEndpoint(locals);
  if (!endpoint) return;

  const raw = url.searchParams.get("returnUrl") || "/";
  // Same-origin paths only, mirroring the endpoint's IsLocalUrl guard: a
  // second "/" or "\" would be a protocol-relative URL (browsers normalize
  // "/\" to "//" in Location headers).
  const returnUrl = /^\/(?![/\\])/.test(raw) ? raw : "/";
  const returnUrlParams = new URL(returnUrl, url.origin).searchParams;

  if (locals.isAuthenticated) {
    redirect(303, stripMarker(returnUrl, url.origin));
  }

  // One-shot guard. The session is host-scoped (cookie domain = the exact host
  // that set it) and only authenticates on a tenant subdomain. Opened on the
  // apex host, the issued session never authenticates, so the auth guard
  // bounces straight back here — and a blind re-redirect would loop forever,
  // flooding the API with 401s from the dashboard load on every pass. If our
  // marker is already present we've had our one attempt: fall through to the
  // passkey UI instead of retrying.
  if (returnUrlParams.has(AUTO_LOGIN_MARKER)) return;

  const marked = appendMarker(returnUrl, url.origin);
  redirect(303, `${endpoint}?redirect=${encodeURIComponent(marked)}`);
};

/**
 * Picks the auto-login endpoint for this request, or null when the login page
 * should render the normal passkey UI.
 */
async function resolveAutoLoginEndpoint(
  locals: App.Locals,
): Promise<string | null> {
  if (env.NOCTURNE_DEV_AUTO_LOGIN === "true") return DEV_LOGIN_ENDPOINT;

  // The share host serves the anonymous read-only view and never honors
  // credentials, so there is no session to be had there.
  if (locals.isShareHost) return null;

  // Fail closed to the passkey UI: an unreachable status call must not bounce a
  // real tenant's owner through an endpoint that will 404.
  try {
    const status = await locals.apiClient.status.getStatus();
    return status?.isDemo ? DEMO_LOGIN_ENDPOINT : null;
  } catch {
    return null;
  }
}

function appendMarker(returnUrl: string, origin: string): string {
  const target = new URL(returnUrl, origin);
  target.searchParams.set(AUTO_LOGIN_MARKER, "1");
  return target.pathname + target.search;
}

function stripMarker(returnUrl: string, origin: string): string {
  const target = new URL(returnUrl, origin);
  target.searchParams.delete(AUTO_LOGIN_MARKER);
  return target.pathname + target.search;
}
