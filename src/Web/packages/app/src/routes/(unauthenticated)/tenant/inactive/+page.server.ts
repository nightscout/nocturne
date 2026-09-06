import type { PageServerLoad } from "./$types";
import { resolveBillingLink } from "./billing-link";

export const load: PageServerLoad = async ({ locals }) => {
  const config = await locals.apiClient.support.getSupportConfig().catch(() => null);

  return { billingLink: resolveBillingLink(config) };
};
