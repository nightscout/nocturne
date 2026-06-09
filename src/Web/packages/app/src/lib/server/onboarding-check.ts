import type { Cookies } from "@sveltejs/kit";

const COOKIE_NAME = "nocturne-setup-complete";
const COOKIE_MAX_AGE = 60 * 60 * 24 * 30; // 30 days

export interface OnboardingResult {
  isComplete: boolean;
}

/**
 * Check if onboarding has been completed.
 * Fast path: cookie check (no API call).
 * Slow path: if cookie missing, query the API and re-set the cookie if complete.
 */
export async function checkOnboarding(
  cookies: Cookies,
  apiClient: { passkey: { getAuthStatus(signal?: AbortSignal): Promise<{ onboardingCompleted?: boolean }> } },
  isSecure: boolean,
): Promise<OnboardingResult> {
  // Fast path — cookie present
  if (cookies.get(COOKIE_NAME) === "true") {
    return { isComplete: true };
  }

  // Slow path — ask the API. Bound the wait so a hung backend can't block the
  // server-side load function indefinitely (which would leave the browser
  // spinning forever on the root layout).
  try {
    const status = await apiClient.passkey.getAuthStatus(AbortSignal.timeout(5000));
    if (status?.onboardingCompleted) {
      // Self-heal: re-set the cookie so subsequent loads are fast
      cookies.set(COOKIE_NAME, "true", {
        path: "/",
        httpOnly: true,
        secure: isSecure,
        sameSite: "lax",
        maxAge: COOKIE_MAX_AGE,
      });
      return { isComplete: true };
    }
    return { isComplete: false };
  } catch {
    // API unreachable or timed out — allow through rather than trapping users
    // in a redirect loop. The setup layout invalidates this cookie on entry,
    // so falling through to /setup here would just re-trigger the slow path
    // on the next navigation.
    return { isComplete: true };
  }
}

/**
 * Clear the onboarding cookie so the next navigation re-evaluates.
 */
export function invalidateOnboardingCache(cookies: Cookies): void {
  cookies.delete(COOKIE_NAME, { path: "/" });
}
