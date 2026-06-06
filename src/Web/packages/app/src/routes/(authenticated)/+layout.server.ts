import { redirect } from "@sveltejs/kit";
import type { LayoutServerLoad } from "./$types";
import { checkOnboarding } from "$lib/server/onboarding-check";

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

export const load: LayoutServerLoad = async ({ locals, cookies, url }) => {
  // Guest sessions bypass onboarding — the data owner's instance is already set up.
  if (!locals.isGuestSession) {
    // Check onboarding first — if the instance needs setup, redirect there
    // regardless of auth state. This covers fresh installs where no tenant
    // or credentials exist yet.
    const onboarding = await checkOnboarding(
      cookies,
      locals.apiClient,
      url.protocol === "https:",
    );
    if (!onboarding.isComplete) {
      throw redirect(303, "/setup");
    }
  }

  // Fetch tenant status once — it drives both the anonymous-access gate and the demo banner.
  // Default to no anonymous access on failure (fail safe: require sign-in rather than over-expose).
  let status: Awaited<ReturnType<typeof locals.apiClient.status.getStatus>> | null = null;
  try {
    status = await locals.apiClient.status.getStatus();
  } catch {
    // Swallow — handled below by the conservative defaults.
  }
  const anonymousReadAccess = status?.anonymousReadAccess ?? false;

  // Redirect anonymous visitors to login when this tenant does not grant anonymous read access
  // (a private tenant). Public tenants keep serving their read-only dashboard, so the shell is
  // never rendered for a visitor who would otherwise see a burst of 401s and a client bounce.
  if (!locals.isAuthenticated || !locals.user) {
    if (!anonymousReadAccess) {
      const returnUrl = encodeURIComponent(url.pathname + url.search);
      throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
    }
  }

  // Enable realtime glucose data for:
  // - Authenticated users with a glucose read permission
  // - Anonymous visitors on a tenant that grants public read access
  // The API enforces authorization on each endpoint as defense in depth.
  const canViewRealtimeData = locals.isAuthenticated
    ? hasGlucoseReadPermission(locals.effectivePermissions ?? [])
    : anonymousReadAccess;

  const isDemo = status?.isDemo ?? false;
  const nextResetAt = status?.nextResetAt ? status.nextResetAt.toISOString() : null;

  return {
    user: locals.user ?? null,
    isGuestSession: locals.isGuestSession ?? false,
    guestExpiresAt: locals.guestExpiresAt ?? null,
    canViewRealtimeData,
    isDemo,
    nextResetAt,
  };
};
