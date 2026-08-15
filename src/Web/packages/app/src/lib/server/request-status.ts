/**
 * The tenant status for the current request, fetched at most once.
 *
 * Several loads need it — the root layout to tell an apex that resolves a tenant from one that
 * does not, the authenticated layout for the anonymous-access gate and the demo banner — and
 * they run on every page load, so without sharing one promise each page costs an extra
 * round-trip to the API for an answer that cannot change mid-request.
 *
 * A failed call resolves to null rather than rejecting: every caller has a conservative default
 * and none of them should turn an unreachable status endpoint into a 500.
 */
export function getRequestStatus(locals: App.Locals): Promise<App.TenantStatus | null> {
  locals.statusPromise ??= locals.apiClient.status
    .getStatus()
    .catch(() => null);
  return locals.statusPromise;
}
