import { command, getRequestEvent } from "$app/server";
import { z } from "zod";

const COOKIE_NAME = "nocturne-setup-complete";
const COOKIE_MAX_AGE = 60 * 60 * 24 * 30; // 30 days

/**
 * Mark onboarding as complete, bypassing the per-step checks.
 * Sets the nocturne-setup-complete cookie so the root layout stops redirecting.
 */
export const markSetupComplete = command(z.void(), async () => {
  const event = getRequestEvent();

  try {
    await event.locals.apiClient.passkey.completeOnboarding();
  } catch {
    // Non-fatal: cookie still works for this browser session
  }

  event.cookies.set(COOKIE_NAME, "true", {
    path: "/",
    httpOnly: true,
    secure: event.url.protocol === "https:",
    sameSite: "lax",
    maxAge: COOKIE_MAX_AGE,
  });

  return { success: true };
});
