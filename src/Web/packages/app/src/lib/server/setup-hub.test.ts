import { describe, expect, it } from "vitest";
import { setupStageFor } from "./setup-hub";

function locals(onboardingCompleted: boolean, scopes: string[]): App.Locals {
  return {
    effectivePermissions: scopes,
    apiClient: { passkey: { getAuthStatus: async () => ({ onboardingCompleted }) } },
  } as unknown as App.Locals;
}

describe("setupStageFor", () => {
  it("keeps a tenant that has not finished the core on the core", async () => {
    await expect(setupStageFor(locals(false, ["*"]))).resolves.toBe("core");
  });

  it("gives an owner past the core the hub, never the core again", async () => {
    await expect(setupStageFor(locals(true, ["*"]))).resolves.toBe("hub");
  });

  it("keeps a member who is not an owner out of the hub", async () => {
    await expect(setupStageFor(locals(true, ["tenant.settings"]))).resolves.toBe("not-owner");
  });
});
