import type { MyPermissionsResponse } from "#lib/api/index.js";
import { isApiOutage } from "#lib/server/api-outage.js";

/**
 * The viewer's granted scopes and history window, which the UI uses to offer only what the
 * viewer can load. `authHandle` resolves them for a signed-in member; a public share link and a
 * guest link never reach that branch, so their grant (the share's shareable read categories, or
 * the scopes on the guest grant) is resolved here instead. Failure leaves the viewer with
 * nothing rather than an over-offer.
 */
export async function resolvePermissions(locals: App.Locals): Promise<MyPermissionsResponse> {
  try {
    return await readPermissions(locals);
  } catch {
    return {};
  }
}

/**
 * {@link resolvePermissions} for a caller that must tell an API outage from a verdict: an outage
 * rejects, and any other refusal from the API resolves to no grant.
 */
export async function readPermissions(locals: App.Locals): Promise<MyPermissionsResponse> {
  if (locals.effectivePermissions) {
    return {
      scopes: locals.effectivePermissions,
      limitTo24Hours: locals.limitTo24Hours ?? false,
      refusedAsDemoSubject: locals.refusedAsDemoSubject ?? false,
    };
  }
  if (!locals.isShareHost && !locals.isGuestSession) return {};

  try {
    return await locals.apiClient.myPermissions.getMyPermissions();
  } catch (error) {
    if (isApiOutage(error)) throw error;
    return {};
  }
}
