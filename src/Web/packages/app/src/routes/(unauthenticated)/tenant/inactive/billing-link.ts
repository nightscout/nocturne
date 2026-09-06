import type { SupportConfigResponse } from "$api/generated/nocturne-api-client";

/**
 * The operator's account address as a link, or null when there is nothing to link to.
 *
 * Api-mode accountBilling is an issue-intake endpoint the app POSTs to, not a page: rendering it
 * as an href would send the visitor to a route with no GET. accountPortal is always a page.
 */
export function resolveBillingLink(
  config: SupportConfigResponse | null | undefined
): { url: string; label: string | null } | null {
  const portal = config?.accountPortal;
  if (portal?.url) return { url: portal.url, label: portal.label ?? null };

  const billing = config?.accountBilling;
  if (billing?.mode !== "redirect" || !billing.url) return null;

  return { url: billing.url, label: billing.label ?? null };
}
