import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/env", () => ({ browser: true, building: false, dev: false, version: "test" }));
vi.mock("#lib/stores/member-storage.js", () => ({ clearIfSignedOut: vi.fn() }));

import { init } from "./hooks.client";
import { authInterceptorState } from "#lib/api/auth-interceptor.js";

afterEach(() => {
  authInterceptorState.reset();
  vi.unstubAllGlobals();
});

describe("client init", () => {
  it("sends a person whose command is refused with 401 to sign in, keeping the page they were on", async () => {
    const location = { hostname: "rhys.nocturne.run", pathname: "/trackers", search: "?id=7", href: "https://rhys.nocturne.run/trackers?id=7" };
    const window = {
      location,
      fetch: vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => Response.json({ type: "error", error: { status: 401, message: "Unauthorized" } })),
      addEventListener: vi.fn(),
    };
    vi.stubGlobal("window", window);

    await init();
    await window.fetch("/_app/remote/abc123/complete", { method: "POST" });

    expect(location.href).toBe(`/auth/login?returnUrl=${encodeURIComponent("/trackers?id=7")}`);
  });
});
