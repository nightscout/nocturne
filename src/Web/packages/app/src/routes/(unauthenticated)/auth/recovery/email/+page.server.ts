import { redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";

export const load: PageServerLoad = async ({ locals }) => {
  let offered: boolean;
  try {
    offered = (await locals.apiClient.passkey.getAuthStatus())?.emailRecoveryOffered === true;
  } catch {
    offered = false;
  }

  if (!offered) redirect(303, "/auth/help");
};
