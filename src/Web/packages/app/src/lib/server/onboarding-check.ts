import type { Cookies } from "@sveltejs/kit";
import type { ApiClient } from "$lib/api";

const COOKIE_NAME = "nocturne-setup-complete";
const COOKIE_MAX_AGE = 60 * 60 * 24 * 30; // 30 days

export interface OnboardingResult {
  isComplete: boolean;
  completedSteps?: string[];
}

/**
 * Check if onboarding is complete.
 * Fast path: cookie is set -> return true immediately (zero API calls).
 * Slow path: cookie absent -> query the API, set cookie if complete.
 */
export async function checkOnboarding(
  apiClient: ApiClient,
  cookies: Cookies
): Promise<OnboardingResult> {
  // Fast path: cookie exists, skip API calls
  if (cookies.get(COOKIE_NAME) === "true") {
    return { isComplete: true };
  }

  try {
    const completedSteps: string[] = [];

    // Query all 4 required checks in parallel using the v4 endpoints
    const [patientRecord, devices, insulins, profileSummary] =
      await Promise.all([
        apiClient.patientRecord.getPatientRecord().catch(() => null),
        apiClient.patientRecord.getDevices().catch(() => null),
        apiClient.patientRecord.getInsulins().catch(() => null),
        apiClient.profile.getProfileSummary().catch(() => null),
      ]);

    // 1. Patient: has a diabetes type set
    const hasPatient = !!patientRecord?.diabetesType;
    if (hasPatient) completedSteps.push("patient");

    // 2. Devices: at least one device marked as current
    const hasDevices =
      (devices ?? []).some((d) => d.isCurrent === true);
    if (hasDevices) completedSteps.push("devices");

    // 3. Insulins: at least one insulin marked as current
    const hasInsulins =
      (insulins ?? []).some((i) => i.isCurrent === true);
    if (hasInsulins) completedSteps.push("insulins");

    // 4. Profile: at least one basal schedule exists
    const hasProfile =
      (profileSummary?.basalSchedules ?? []).length > 0;
    if (hasProfile) completedSteps.push("profile");

    const isComplete =
      hasPatient && hasDevices && hasInsulins && hasProfile;

    // Set the cookie if onboarding is complete so future requests skip API calls
    if (isComplete) {
      cookies.set(COOKIE_NAME, "true", {
        path: "/",
        httpOnly: true,
        secure: true,
        sameSite: "lax",
        maxAge: COOKIE_MAX_AGE,
      });
    }

    return { isComplete, completedSteps };
  } catch (error) {
    // Fail-open: if API calls fail, don't trap the user in the setup wizard
    console.error("Onboarding check failed, failing open:", error);
    return { isComplete: true };
  }
}

/**
 * Clear the onboarding cookie so the next navigation re-evaluates.
 * Call this when entering the setup wizard.
 */
export function invalidateOnboardingCache(cookies: Cookies): void {
  cookies.delete(COOKIE_NAME, { path: "/" });
}
