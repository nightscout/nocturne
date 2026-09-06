/**
 * Remote function to get the current tenant ID.
 * Shared across settings pages that need the current tenant context.
 */
import { getRequestEvent, query } from "$app/server";
import { error, redirect } from "@sveltejs/kit";
import { classifyRequestHost } from "$lib/server/tenantless-host";
import { activeTenants } from "$lib/utils/tenant-host";

/**
 * Get the tenant the request's host serves, of those the authenticated user can reach.
 *
 * A host that names no tenant — the apex of a single-tenant install, where the API resolves the
 * sole tenant itself — falls back to the visitor's oldest membership.
 */
export const getCurrentTenantId = query(async () => {
  const { locals, url, request } = getRequestEvent();

  if (!locals.isAuthenticated) {
    throw redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`);
  }

  const apiClient = locals.apiClient;
  try {
    const tenants = activeTenants(await apiClient.myTenants.getMyTenants());
    const { slug } = classifyRequestHost(request);

    return (tenants.find((t) => t.slug === slug) ?? tenants[0])?.id ?? null;
  } catch (err) {
    const status = (err as any)?.status;
    if (status === 401) {
      throw redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`);
    }
    if (status === 403) throw error(403, "Forbidden");
    console.error("Error in getCurrentTenantId:", err);
    throw error(500, "Failed to get current tenant");
  }
});
