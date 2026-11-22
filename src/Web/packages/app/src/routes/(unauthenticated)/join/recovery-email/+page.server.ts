import { isRedirect, redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";
import { safeReturnUrl } from "#lib/navigation/safe-return-url.js";

/**
 * The step after joining that asks for a recovery email. It shows only for a signed-in member with
 * no address, where the site can send the confirming email, and the reminder is not dismissed. Any
 * other answer, or a failed read, goes straight on, so the step never gates joining.
 */
export const load: PageServerLoad = async ({ locals, url }) => {
  const next = safeReturnUrl(url.searchParams.get("next"));

  if (!locals.user) redirect(303, next);

  try {
    const [recovery, prompt] = await Promise.all([
      locals.apiClient.recoveryEmail.getRecoveryEmail(),
      locals.apiClient.recoveryEmailPrompt.getRecoveryEmailPrompt(),
    ]);
    if (!recovery.available || recovery.email || !prompt.show) redirect(303, next);
  } catch (error) {
    if (isRedirect(error)) throw error;
    console.error("Failed to read the recovery email step:", error);
    redirect(303, next);
  }

  return { next };
};
