import { redirect } from "@sveltejs/kit";
import { env } from "$env/dynamic/private";
import type { PageServerLoad } from "./$types";

// Marker appended to returnUrl so a single auto-login attempt can be detected
// after it bounces back. It survives the round-trip because the auth guard
// rebuilds returnUrl from pathname + search.
const AUTO_LOGIN_MARKER = "__autologin";

// Dev auto-login. With NOCTURNE_DEV_AUTO_LOGIN=true (forwarded from the host
// environment by the Aspire AppHost, run mode only), the login page redirects
// through the dev-only session endpoint instead of rendering the passkey UI.
// Every auth guard funnels to /auth/login?returnUrl=..., so this one hook
// covers all of them: the endpoint issues a real session for the tenant's
// first owner member, sets the normal cookies, and bounces back to returnUrl.
// The endpoint exists only when the API runs in Development; elsewhere the
// redirect lands on a 404, never a session.
export const load: PageServerLoad = async ({ url, locals }) => {
  if (env.NOCTURNE_DEV_AUTO_LOGIN !== "true") return;

  const raw = url.searchParams.get("returnUrl") || "/";
  // Same-origin paths only, mirroring the endpoint's IsLocalUrl guard: a
  // second "/" or "\" would be a protocol-relative URL (browsers normalize
  // "/\" to "//" in Location headers).
  const returnUrl = /^\/(?![/\\])/.test(raw) ? raw : "/";
  const returnUrlParams = new URL(returnUrl, url.origin).searchParams;

  if (locals.isAuthenticated) {
    redirect(303, stripMarker(returnUrl, url.origin));
  }

  // One-shot guard. The dev session is host-scoped (cookie domain = the exact
  // host that set it) and only authenticates on a tenant subdomain. Opened on
  // the apex host, the issued session never authenticates, so the auth guard
  // bounces straight back here — and a blind re-redirect would loop forever,
  // flooding the API with 401s from the dashboard load on every pass. If our
  // marker is already present we've had our one attempt: fall through to the
  // passkey UI instead of retrying.
  if (returnUrlParams.has(AUTO_LOGIN_MARKER)) return;

  const marked = appendMarker(returnUrl, url.origin);
  redirect(303, `/api/v4/dev-only/auth/login?redirect=${encodeURIComponent(marked)}`);
};

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
