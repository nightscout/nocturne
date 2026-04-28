import { redirect } from "@sveltejs/kit";
import type { LayoutServerLoad } from "./$types";
import { checkOnboarding } from "$lib/server/onboarding-check";

export const load: LayoutServerLoad = async ({ locals, cookies, url }) => {
  // Guest sessions bypass onboarding — the data owner's instance is already set up.
  if (!locals.isGuestSession) {
    // Check onboarding first — if the instance needs setup, redirect there
    // regardless of auth state. This covers fresh installs where no tenant
    // or credentials exist yet.
    const onboarding = await checkOnboarding(
      cookies,
      locals.apiClient,
      url.protocol === "https:",
    );
    if (!onboarding.isComplete) {
      throw redirect(303, "/setup");
    }
  }

  if (!locals.isAuthenticated || !locals.user) {
    // Guest sessions with a valid cookie are authenticated in hooks;
    // if we still land here, the session is invalid — send to the guest page.
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }

  return {
    user: locals.user,
    isGuestSession: locals.isGuestSession ?? false,
    guestExpiresAt: locals.guestExpiresAt ?? null,
  };
};
