import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it, vi } from "vitest";

vi.mock("$app/env", () => ({ browser: false, building: false, dev: false }));
import { isRedirect, type Cookies } from "@sveltejs/kit";
import { load as shellUniversalLoad } from "../../+layout";
import { load as shellServerLoad } from "../../+layout.server";
import { load as wallServerLoad } from "./+layout.server";
import { load as wallUniversalLoad } from "./+layout";

type ServerEvent = Parameters<typeof wallServerLoad>[0];

function eventFor(signedIn: boolean) {
  return {
    locals: {
      isGuestSession: false,
      isShareHost: false,
      isAuthenticated: signedIn,
      user: signedIn ? { subjectId: "s1", name: "Sam" } : null,
      effectivePermissions: ["*"],
      apiClient: {
        status: { getStatus: async () => ({ status: "ok", tenantSlug: "acme" }) },
        passkey: { getAuthStatus: async () => ({ onboardingCompleted: true }) },
        backupSignInPrompt: { getBackupSignInPrompt: async () => ({ show: false }) },
        myTenants: { getMyTenants: async () => [] },
      },
    },
    cookies: { get: () => undefined, set: () => {}, delete: () => {} } as unknown as Cookies,
    url: new URL("https://acme.nocturne.run/tenants/wall"),
    untrack: <T>(fn: () => T) => fn(),
    depends: () => {},
    parent: async () => ({ tenantless: false }),
  } as unknown as ServerEvent;
}

const here = (file: string) => fileURLToPath(new URL(file, import.meta.url));

describe("people wall route placement", () => {
  it("resets to the root layout with a layout of its own, never a page reset that skips the loads", () => {
    expect(existsSync(here("./+layout@.svelte"))).toBe(true);
    expect(existsSync(here("./+page.svelte"))).toBe(true);
    expect(existsSync(here("./+page@.svelte"))).toBe(false);
  });

  it("re-exports both of the authenticated shell's loads", () => {
    expect(readFileSync(here("./+layout.server.ts"), "utf8")).toMatch(
      /export \{ load \} from "\.\.\/\.\.\/\+layout\.server"/
    );
    expect(readFileSync(here("./+layout.ts"), "utf8")).toMatch(
      /export \{ load \} from "\.\.\/\.\.\/\+layout"/
    );
  });
});

describe("people wall route guard", () => {
  it("runs the authenticated shell's own loads, not copies of them", () => {
    expect(wallServerLoad).toBe(shellServerLoad);
    expect(wallUniversalLoad).toBe(shellUniversalLoad);
  });

  it("sends a signed-out visitor to sign in and back to the wall", async () => {
    let location: string | null = null;
    try {
      await wallServerLoad(eventFor(false));
    } catch (error) {
      if (!isRedirect(error)) throw error;
      location = error.location;
    }

    expect(location).toBe("/auth/login?returnUrl=%2Ftenants%2Fwall");
  });

  it("lets a signed-in visitor through", async () => {
    await expect(wallServerLoad(eventFor(true))).resolves.toMatchObject({
      user: { name: "Sam" },
    });
  });
});
