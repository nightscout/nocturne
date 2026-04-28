import { command, getRequestEvent } from "$app/server";
import { z } from "zod";
export { validateSlug as validateSetupSlug } from "$lib/api/generated/myTenants.generated.remote";
export { createTenant as setupTenant, ownerOptions as setupOwnerOptions, ownerComplete as setupOwnerComplete, ownerOidc as setupOwnerOidc, validateUsername as validateSetupUsername } from "$lib/api/generated/setups.generated.remote";

const COOKIE_NAME = "nocturne-setup-complete";
const COOKIE_MAX_AGE = 60 * 60 * 24 * 30; // 30 days

const SETUP_TENANT_COOKIE = "nocturne-setup-tenant";

/**
 * Store the newly-created tenant slug so subsequent API calls from the apex
 * domain include the correct subdomain in X-Forwarded-Host.
 */
export const setSetupTenantSlug = command(z.string(), async (slug) => {
  const event = getRequestEvent();

  event.cookies.set(SETUP_TENANT_COOKIE, slug, {
    path: "/",
    httpOnly: true,
    secure: event.url.protocol === "https:",
    sameSite: "lax",
    maxAge: 3600, // 1 hour — enough to complete setup
  });

  return { success: true };
});

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

  // Clean up the setup tenant slug cookie — no longer needed
  event.cookies.delete(SETUP_TENANT_COOKIE, { path: "/" });

  return { success: true };
});
