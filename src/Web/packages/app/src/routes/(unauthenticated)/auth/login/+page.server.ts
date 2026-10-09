import { redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";
import { GUEST_CODE_DISMISSED_COOKIE } from "$lib/components/auth/guest-code-dismissal";
import { safeReturnUrl } from "$lib/server/return-url";
import { autoLoginEndpoint, autoLoginRedirect, withMarker } from "$lib/server/auto-login";

// Every auth guard funnels to /auth/login?returnUrl=..., so this one page covers
// auto-login for all of them; the authenticated layout also sends anonymous
// visitors to the endpoint directly when it already knows it. See auto-login.ts.
export const load: PageServerLoad = async ({ url, locals, cookies, parent }) => {
  const endpoint = await resolveAutoLoginEndpoint(locals);
  if (!endpoint) {
    const { tenantless } = await parent();
    return {
      guestCodePending: await hasPendingGuestCode(locals, tenantless),
      guestCodeDismissed: cookies.get(GUEST_CODE_DISMISSED_COOKIE) === "1",
    };
  }

  const returnUrl = safeReturnUrl(url.searchParams.get("returnUrl"));

  if (locals.isAuthenticated) {
    redirect(303, withMarker(returnUrl, url.origin, false));
  }

  // Null once the one attempt has been spent: fall through to the passkey UI.
  const target = autoLoginRedirect(endpoint, returnUrl, url.origin);
  if (target) redirect(303, target);
};

/**
 * Picks the auto-login endpoint for this request, or null when the login page
 * should render the normal passkey UI.
 */
async function resolveAutoLoginEndpoint(
  locals: App.Locals,
): Promise<string | null> {
  // Neither dev auto-login nor the share host depends on the tenant's status.
  const withoutStatus = autoLoginEndpoint(false, locals.isShareHost);
  if (withoutStatus || locals.isShareHost) return withoutStatus;

  // Fail closed to the passkey UI: an unreachable status call must not bounce a
  // real tenant's owner through an endpoint that will 404.
  try {
    const status = await locals.apiClient.status.getStatus();
    return autoLoginEndpoint(status?.isDemo, false);
  } catch {
    return null;
  }
}

/**
 * Whether the tenant has an unredeemed guest code, in which case the page opens
 * on code entry so a guest can be sent to the bare site rather than /guest.
 * The share host has no sessions and a tenantless host has no codes.
 */
async function hasPendingGuestCode(
  locals: App.Locals,
  tenantless: boolean,
): Promise<boolean> {
  if (tenantless || locals.isShareHost) return false;

  try {
    const { pending } = await locals.apiClient.guestLink.getGuestCodePending();
    return pending === true;
  } catch {
    return false;
  }
}
