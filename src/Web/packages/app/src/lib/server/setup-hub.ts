import { satisfiesScope } from "$lib/authorization/scopes";

/**
 * What `/setup` is for a signed-in viewer: the onboarding core while the tenant has not finished
 * it, then the setup hub, which only an owner can use. A finished core is never offered again,
 * so a status that cannot be read fails the page rather than guessing.
 */
export async function setupStageFor(
  locals: App.Locals
): Promise<"core" | "hub" | "not-owner"> {
  const status = await locals.apiClient.passkey.getAuthStatus();
  if (!status?.onboardingCompleted) return "core";
  return satisfiesScope(locals.effectivePermissions ?? [], "*") ? "hub" : "not-owner";
}
