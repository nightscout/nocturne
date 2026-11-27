import { describe, expect, it, vi } from "vitest";

vi.mock("#lib/server/setup-hub.js", () => ({ setupStageFor: async () => "hub" }));

import { load } from "./+layout.server";

type LoadEvent = Parameters<typeof load>[0];

async function redirectFor(url: string) {
  const event = { locals: { isAuthenticated: false }, url: new URL(url) } as unknown as LoadEvent;
  try {
    await load(event);
  } catch (thrown) {
    return thrown;
  }
  throw new Error("the load returned without redirecting");
}

describe("the guided setup layout without a session", () => {
  it("sends the visitor to sign in, returning to the item with its query", async () => {
    const redirected = await redirectFor("https://acme.nocturne.test/setup/therapy?step=2");

    expect(redirected).toMatchObject({
      status: 302,
      location: `/auth/login?returnUrl=${encodeURIComponent("/setup/therapy?step=2")}`,
    });
  });
});
