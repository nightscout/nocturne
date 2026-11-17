/**
 * Remote function to get the current tenant ID.
 * Shared across settings pages that need the current tenant context.
 */
import { getRequestEvent, query } from "$app/server";
import { error, redirect } from "@sveltejs/kit";
import { classifyRequestHost } from "#lib/server/tenantless-host.js";
import { errorStatus } from "#lib/forms/submit-error.js";

/**
 * Get the tenant the request's host serves, of those the authenticated user can reach.
 *
 * A host that names no tenant — the apex of a single-tenant install, where the API resolves the
 * sole tenant itself — falls back to the first tenant the visitor owns, or to their oldest
 * membership where they own none.
 */
export const getCurrentTenantId = query(async () => {
  const { locals, request } = getRequestEvent();

  if (!locals.isAuthenticated) {
    throw redirect(302, "/auth/login");
  }

  const apiClient = locals.apiClient;
  try {
    const tenants = await apiClient.myTenants.getMyTenants(true);
    const { slug } = classifyRequestHost(request);

    return (tenants.find((t) => t.slug === slug) ?? tenants[0])?.id ?? null;
  } catch (err) {
    const status = errorStatus(err);
    if (status === 401) {
      throw redirect(302, "/auth/login");
    }
    if (status === 403) throw error(403, "Forbidden");
    console.error("Error in getCurrentTenantId:", err);
    throw error(500, "Failed to get current tenant");
  }
});
