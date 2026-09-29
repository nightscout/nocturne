import { satisfiesScope } from "$lib/authorization/scopes";

/** Bounds each status read, as `checkOnboarding` does, so a hung API fails the page instead. */
const STATUS_TIMEOUT_MS = 5000;

/**
 * What `/setup` is for a signed-in viewer: the onboarding core while the tenant has not finished
 * it, then the setup hub, which only an owner can use. A finished core is never offered again,
 * so a status that cannot be read, even on a second try, fails the page rather than guessing.
 */
export async function setupStageFor(
  locals: App.Locals
): Promise<"core" | "hub" | "not-owner"> {
  const read = () =>
    locals.apiClient.passkey.getAuthStatus(AbortSignal.timeout(STATUS_TIMEOUT_MS));
  const status = await read().catch(read);
  if (!status?.onboardingCompleted) return "core";
  return satisfiesScope(locals.effectivePermissions ?? [], "*") ? "hub" : "not-owner";
}
