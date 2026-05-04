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

  // Slow path — ask the API
  try {
    const status = await apiClient.passkey.getAuthStatus();
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
  } catch {
    // API unreachable — allow through to avoid blocking authenticated users
  }

  return { isComplete: false };
}

/**
 * Clear the onboarding cookie so the next navigation re-evaluates.
 */
export function invalidateOnboardingCache(cookies: Cookies): void {
  cookies.delete(COOKIE_NAME, { path: "/" });
}
