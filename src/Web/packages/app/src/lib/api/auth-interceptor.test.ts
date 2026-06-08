import { describe, it, expect, vi, beforeEach } from "vitest";

// Force browser mode so redirectToLogin runs its real logic — the node stub
// sets browser=false to skip DOM side-effects, which would short-circuit it.
vi.mock("$app/environment", () => ({
  browser: true,
  building: false,
  dev: false,
  version: "test",
}));

import { authInterceptorState } from "./auth-interceptor";

function stubLocation(hostname: string) {
  const location = { hostname, pathname: "/", search: "", href: "" };
  vi.stubGlobal("window", { location });
  return location;
}

describe("authInterceptorState.redirectToLogin", () => {
  beforeEach(() => {
    authInterceptorState.reset();
    vi.unstubAllGlobals();
  });

  it("does NOT redirect public share-host viewers to login on 401", () => {
    // A share-link visitor is anonymous by design; a 401 on a category the tenant
    // didn't share must not bounce them to a login they can't complete.
    const location = stubLocation("k7m2q9x4r3wt.share.nocturne.run");
    authInterceptorState.redirectToLogin();
    expect(location.href).toBe("");
  });

  it("still redirects normal tenant-host visitors to login on 401", () => {
    const location = stubLocation("rhys.nocturne.run");
    authInterceptorState.redirectToLogin();
    expect(location.href).toContain("/auth/login?returnUrl=");
  });

  it("does NOT redirect guest sessions (existing exemption preserved)", () => {
    const location = stubLocation("rhys.nocturne.run");
    authInterceptorState.setGuestSession(true);
    authInterceptorState.redirectToLogin();
    expect(location.href).toBe("");
  });
});
