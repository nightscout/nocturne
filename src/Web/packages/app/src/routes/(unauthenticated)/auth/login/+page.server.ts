import { redirect } from "@sveltejs/kit";
import { env } from "$env/dynamic/private";
import type { PageServerLoad } from "./$types";

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

  if (locals.isAuthenticated) {
    redirect(303, returnUrl);
  }
  redirect(303, `/api/v4/dev-only/auth/login?redirect=${encodeURIComponent(returnUrl)}`);
};
