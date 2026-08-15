import { redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";
import { SETUP_TENANT_COOKIE } from "$lib/server/request-host";
import { isFreshInstallError } from "$lib/server/onboarding-check";

export const load: PageServerLoad = async ({ locals, cookies, parent }) => {
  const setupTenantSlug = cookies.get(SETUP_TENANT_COOKIE) ?? null;
  const { tenantless } = await parent();

  if (locals.isAuthenticated) {
    return { setupRequired: false, tenantExists: true, setupTenantSlug };
  }

  // Determine if setup is needed:
  // - 503 from the API means no tenant exists yet (fresh install)
  // - 200 with setupRequired means tenant exists but has no credentials
  // - 200 without setupRequired means fully set up → redirect to login
  try {
    const status = await locals.apiClient.passkey.getAuthStatus();
    if (status?.setupRequired) {
      // Tenant exists (we got a 200, not 503) but has no credentials yet.
      // User abandoned after creating tenant — skip to account creation.
      return { setupRequired: true, tenantExists: true, setupTenantSlug };
    }
  } catch (err) {
    // 503 means zero tenants exist — setup is definitely required
    if (isFreshInstallError(err)) {
      return { setupRequired: true, tenantExists: false, setupTenantSlug };
    }
    // On a host that serves the cross-tenant dashboard the auth-status call has no tenant to
    // report on and fails for that reason alone, not because the instance is unconfigured. The
    // fresh-install wizard on a deployment that already has tenants would be actively
    // misleading, so send the visitor to the sign-in it actually needs.
    if (tenantless) {
      redirect(302, "/auth/login?returnUrl=/setup");
    }
    // Any other API failure (network error, 500, etc.) — also show setup,
    // since we can't confirm the instance is healthy
    return { setupRequired: true, tenantExists: false, setupTenantSlug };
  }

  redirect(302, "/auth/login?returnUrl=/setup");
};
