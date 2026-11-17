import { describe, expect, it, vi } from "vitest";
import { isRedirect } from "@sveltejs/kit";

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: {
      apiClient: { guestLink: { activateGuestLink: async () => ({}) } },
    },
  }),
  form: (_schema: unknown, fn: unknown) => fn,
}));

const { activateGuestCode } = await import("./guest.remote");

type Handler = (
  data: { code: string; returnUrl?: string },
  issue: unknown
) => Promise<never>;

async function redirectFor(returnUrl: string | undefined): Promise<string> {
  try {
    await (activateGuestCode as unknown as Handler)(
      { code: "ABC-DEFG", returnUrl },
      {}
    );
  } catch (thrown) {
    if (isRedirect(thrown)) return thrown.location;
    throw thrown;
  }
  throw new Error("activation did not redirect");
}

describe("activateGuestCode", () => {
  it("lands on the return path with the welcome marker", async () => {
    expect(await redirectFor("/reports?range=7d#tir")).toBe(
      "/reports?range=7d&welcome=1#tir"
    );
  });

  it("lands on the dashboard with the marker when no return path is given", async () => {
    expect(await redirectFor(undefined)).toBe("/?welcome=1");
  });

  it.each(["/.//evil.test", "/..//evil.test", "/a/..//evil.test", "/%2e//evil.test"])(
    "never redirects off-site for %s",
    async (returnUrl) => {
      expect(await redirectFor(returnUrl)).toBe("/?welcome=1");
    }
  );
});
