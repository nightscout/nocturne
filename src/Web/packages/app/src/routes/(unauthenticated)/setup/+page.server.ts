import { redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";

export const load: PageServerLoad = async ({ locals, cookies }) => {
  const setupTenantSlug = cookies.get("nocturne-setup-tenant") ?? null;

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
    if (err && typeof err === "object" && "status" in err && err.status === 503) {
      return { setupRequired: true, tenantExists: false, setupTenantSlug };
    }
    // Any other API failure (network error, 500, etc.) — also show setup,
    // since we can't confirm the instance is healthy
    return { setupRequired: true, tenantExists: false, setupTenantSlug };
  }

  redirect(302, "/auth/login?returnUrl=/setup");
};
