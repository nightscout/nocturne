import type { PageServerLoad, Actions } from "./$types";
import { redirect, fail } from "@sveltejs/kit";

export const load: PageServerLoad = async ({ params, locals }) => {
  const { token } = params;
  const { apiClient } = locals;

  try {
    const invite = await apiClient.oauth.getInviteInfo(token);

    return {
      invite,
      token,
      isAuthenticated: locals.isAuthenticated ?? false,
    };
  } catch {
    return {
      invite: null,
      token,
      error: "Invite not found or has expired.",
      isAuthenticated: locals.isAuthenticated ?? false,
    };
  }
};

export const actions: Actions = {
  accept: async ({ params, locals }) => {
    if (!locals.isAuthenticated) {
      const returnUrl = `/invite/${params.token}`;
      throw redirect(302, `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
    }

    const { token } = params;
    const { apiClient } = locals;

    try {
      await apiClient.oauth.acceptInvite(token);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to accept invite.";
      return fail(400, { error: errorMessage });
    }

    throw redirect(302, "/");
  },
};
