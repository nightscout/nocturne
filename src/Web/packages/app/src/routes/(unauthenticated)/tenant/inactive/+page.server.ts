import type { SupportChannelConfig } from "$api/generated/nocturne-api-client";
import type { PageServerLoad } from "./$types";

/**
 * The operator's billing address as a link, or null when there is nothing to link to.
 *
 * Api mode is an issue-intake endpoint the app POSTs to, not a page: rendering it as an href
 * would send the visitor to a route with no GET.
 */
export function resolveBillingLink(
  config: SupportChannelConfig | null | undefined
): { url: string; label: string | null } | null {
  if (config?.mode !== "redirect" || !config.url) return null;

  return { url: config.url, label: config.label ?? null };
}

export const load: PageServerLoad = async ({ locals }) => {
  const config = await locals.apiClient.support.getSupportConfig().catch(() => null);

  return { billingLink: resolveBillingLink(config?.accountBilling) };
};
