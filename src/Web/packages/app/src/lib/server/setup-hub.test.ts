import { describe, expect, it, vi } from "vitest";
import { setupStageFor } from "./setup-hub";

type GetAuthStatus = (signal?: AbortSignal) => Promise<{ onboardingCompleted?: boolean }>;

function localsWith(getAuthStatus: GetAuthStatus, scopes: string[] = ["*"]): App.Locals {
  return {
    effectivePermissions: scopes,
    apiClient: { passkey: { getAuthStatus } },
  } as unknown as App.Locals;
}

const answering = (onboardingCompleted: boolean) => async () => ({ onboardingCompleted });

describe("setupStageFor", () => {
  it("keeps a tenant that has not finished the core on the core", async () => {
    await expect(setupStageFor(localsWith(answering(false)))).resolves.toBe("core");
  });

  it("gives an owner past the core the hub, never the core again", async () => {
    await expect(setupStageFor(localsWith(answering(true)))).resolves.toBe("hub");
  });

  it("keeps a member who is not an owner out of the hub", async () => {
    await expect(setupStageFor(localsWith(answering(true), ["tenant.settings"]))).resolves.toBe(
      "not-owner"
    );
  });

  it("bounds every status read with a timeout, so a hung API cannot hang the page", async () => {
    const getAuthStatus = vi.fn<GetAuthStatus>(async (signal) => {
      expect(signal).toBeInstanceOf(AbortSignal);
      return { onboardingCompleted: true };
    });

    await setupStageFor(localsWith(getAuthStatus));

    expect(getAuthStatus).toHaveBeenCalledOnce();
  });

  it("tries the status once more before failing the page", async () => {
    const recovering = vi
      .fn<GetAuthStatus>()
      .mockRejectedValueOnce(new DOMException("timed out", "TimeoutError"))
      .mockResolvedValueOnce({ onboardingCompleted: true });
    await expect(setupStageFor(localsWith(recovering))).resolves.toBe("hub");

    const down = vi.fn<GetAuthStatus>().mockRejectedValue(new Error("down"));
    await expect(setupStageFor(localsWith(down))).rejects.toThrow("down");
    expect(down).toHaveBeenCalledTimes(2);
  });
});
