import { redirect } from "@sveltejs/kit";
import type { LayoutServerLoad } from "./$types";
import { checkOnboarding } from "$lib/server/onboarding-check";
import { getRequestStatus } from "$lib/server/request-status";
import { isTenantlessRoute } from "$lib/navigation/tenantless-navigation";
import { SHARE_UNAVAILABLE_PATH } from "$lib/share-host";
import { toIsoString } from "$lib/utils/api-date";

/** Permissions that grant read access to glucose data (mirrors API's CanRead + OAuth scopes). */
const GLUCOSE_READ_PERMISSIONS = [
  "*",
  "api:*",
  "api:*:read",
  "readable",
  "glucose.read",
  "glucose.readwrite",
  "health.read",
  "health.readwrite",
];

function hasGlucoseReadPermission(permissions: string[]): boolean {
  return permissions.some((p) => GLUCOSE_READ_PERMISSIONS.includes(p));
}

export const load: LayoutServerLoad = async ({ locals, cookies, url, parent }) => {
  // Resolved by the root layout from the request host and the API's answer for the apex. Read
  // first because it qualifies the setup redirect below as well as the route guard further down.
  const { tenantless } = await parent();

  // Tenant status drives the anonymous-access gate and the demo banner. Shared with the root
  // layout's tenantless check, and null on failure — default to no anonymous access then
  // (fail safe: require sign-in rather than over-expose).
  const status = await getRequestStatus(locals);
  const anonymousReadAccess = status?.anonymousReadAccess ?? false;

  // Public read is served only on the share host ({token}.share.{baseDomain}); the bare tenant
  // host stays login-only even when the tenant has sharing enabled. The API enforces this too
  // (it grants public read only under ShareAccess); this gate just avoids rendering the shell and
  // then bursting 401s on the bare host.
  // Security headers for the share host (Referrer-Policy, X-Robots-Tag) are applied for every
  // response in hooks.server.ts (shareHostSecurityHandle).
  const publicViewAllowed = locals.isShareHost && anonymousReadAccess;

  // Resolved above every redirect below, because none of them reaches anywhere a share host can
  // act on: /auth/login wants a session this host holds none of by construction, and /setup offers
  // a first-run wizard on an instance a share link only resolves on once it is past setup — which
  // reads as "this deployment is broken" to whoever was handed the link. A share host that grants
  // nothing has a link that was rotated, disabled, or never valid; it is also what a status call
  // that simply failed looks like from here, so the page it lands on claims neither.
  if (locals.isShareHost && !publicViewAllowed) {
    throw redirect(303, SHARE_UNAVAILABLE_PATH);
  }

  // Guest sessions bypass onboarding — the data owner's instance is already set up.
  if (!locals.isGuestSession && !publicViewAllowed) {
    // If the instance needs setup, redirect there regardless of auth state.
    // This covers fresh installs where no tenant or credentials exist yet.
    const onboarding = await checkOnboarding(
      cookies,
      locals.apiClient,
      url.protocol === "https:",
    );
    if (!onboarding.isComplete) {
      throw redirect(303, "/setup");
    }
  }

  // A fresh instance with no resolved tenant reports "setup_required" — send it to setup rather
  // than bouncing an anonymous visitor to login.
  //
  // Not on a tenantless host. So does every apex of a multi-tenant install and every reserved
  // dashboard slug, because the status endpoint reports "setup_required" for any request that
  // resolves no tenant, whatever the reason. Redirecting those would put the fresh-install wizard
  // in front of a production deployment that already has tenants, and the dashboard would never
  // render at all. A genuinely fresh install is caught above instead: checkOnboarding reads the
  // 503 the API serves when no tenant exists.
  if (!tenantless && !publicViewAllowed && status?.status === "setup_required") {
    throw redirect(303, "/setup");
  }

  // Redirect anonymous visitors to login unless they are on the share host of a tenant with
  // sharing enabled. The share host keeps serving its read-only dashboard, so the shell is never
  // rendered for a visitor who would otherwise see a burst of 401s and a client bounce.
  if (!locals.isAuthenticated || !locals.user) {
    if (!publicViewAllowed) {
      const returnUrl = encodeURIComponent(url.pathname + url.search);
      throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
    }
  }

  // A tenantless host resolves no tenant, so a tenant-scoped page would render its shell and
  // then 404 against the API. The nav hides those entries; this catches direct navigation and
  // stale links, and stays below the login redirect so it can never pre-empt it.
  if (tenantless && !isTenantlessRoute(url.pathname)) {
    throw redirect(303, "/");
  }

  // Enable realtime glucose data for:
  // - Authenticated users with a glucose read permission
  // - Anonymous visitors on the share host of a tenant that grants public read access
  // The API enforces authorization on each endpoint as defense in depth.
  //
  // Never on a tenantless host: there is no tenant whose glucose could be read, and the
  // permissions reported there are the raw JWT scopes rather than tenant-derived ones — so a
  // platform admin carrying "*" would otherwise open a websocket that reconnects forever and
  // burst a day of entries, devicestatus, profile and tracker reads against 404s.
  const canViewRealtimeData = tenantless
    ? false
    : locals.isAuthenticated
      ? hasGlucoseReadPermission(locals.effectivePermissions ?? [])
      : publicViewAllowed;

  const isDemo = status?.isDemo ?? false;
  const nextResetAt = toIsoString(status?.nextResetAt);

  return {
    user: locals.user ?? null,
    isGuestSession: locals.isGuestSession ?? false,
    guestExpiresAt: locals.guestExpiresAt ?? null,
    canViewRealtimeData,
    isDemo,
    nextResetAt,
  };
};
