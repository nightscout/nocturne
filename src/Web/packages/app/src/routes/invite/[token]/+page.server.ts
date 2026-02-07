import type { PageServerLoad, Actions } from "./$types";
import { redirect, fail } from "@sveltejs/kit";

// TODO: Replace with actual API client calls when NSwag client is regenerated
// These are stubs that will be replaced with proper remote function calls

interface InviteInfo {
  ownerName: string | null;
  ownerEmail: string | null;
  scopes: string[];
  label: string | null;
  expiresAt: string;
  isValid: boolean;
  isExpired: boolean;
  isRevoked: boolean;
}

export const load: PageServerLoad = async ({ params, fetch, locals }) => {
  const { token } = params;

  // Fetch invite info from API
  // TODO: Replace with: const invite = await getInviteInfo({ token });
  const response = await fetch(`/api/oauth/invites/${token}/info`);

  if (!response.ok) {
    return {
      invite: null,
      error: "Invite not found or has expired.",
    };
  }

  const invite: InviteInfo = await response.json();

  return {
    invite,
    token,
    isAuthenticated: locals.session?.isAuthenticated ?? false,
  };
};

export const actions: Actions = {
  accept: async ({ params, fetch, locals }) => {
    if (!locals.session?.isAuthenticated) {
      // Redirect to login with return URL
      const returnUrl = `/invite/${params.token}`;
      redirect(302, `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
    }

    const { token } = params;

    // Accept the invite
    // TODO: Replace with: const result = await acceptInvite({ token });
    const response = await fetch(`/api/oauth/invites/${token}/accept`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
    });

    if (!response.ok) {
      const error = await response.json();
      return fail(400, {
        error: error.errorDescription ?? "Failed to accept invite.",
      });
    }

    // Redirect to dashboard on success
    redirect(302, "/");
  },
};
