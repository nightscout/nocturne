import { describe, expect, it } from "vitest";
import { isRedirect } from "@sveltejs/kit";
import { load } from "./+page.server";

type LoadEvent = Parameters<typeof load>[0];

async function outcome(getAuthStatus: () => Promise<{ emailRecoveryOffered?: boolean } | undefined>) {
  const event = { locals: { apiClient: { passkey: { getAuthStatus } } } } as unknown as LoadEvent;
  try {
    await load(event);
    return "renders";
  } catch (thrown) {
    if (isRedirect(thrown)) return thrown.location;
    throw thrown;
  }
}

describe("recovery by email page load", () => {
  it("renders when the instance offers recovery by email", async () => {
    expect(await outcome(async () => ({ emailRecoveryOffered: true }))).toBe("renders");
  });

  it("sends a visitor to the help page when it is not offered", async () => {
    expect(await outcome(async () => ({ emailRecoveryOffered: false }))).toBe("/auth/help");
  });

  it("sends a visitor to the help page when the status cannot be read", async () => {
    expect(
      await outcome(async () => {
        throw new Error("unreachable");
      }),
    ).toBe("/auth/help");
  });
});
