import { describe, it, expect, vi, beforeEach } from "vitest";

// Force browser mode so redirectToLogin runs its real logic — the node stub
// sets browser=false to skip DOM side-effects, which would short-circuit it.
vi.mock("$app/env", () => ({
  browser: true,
  building: false,
  dev: false,
  version: "test",
}));

import { authInterceptorState, createRemoteSignInFetch } from "./auth-interceptor";

function stubLocation(hostname: string, pathname = "/", search = "") {
  const location = { hostname, pathname, search, href: "" };
  vi.stubGlobal("window", { location });
  return location;
}

function on(hostname: string, pathname: string, search: string) {
  const location = { hostname, pathname, search, href: `https://${hostname}${pathname}${search}` };
  vi.stubGlobal("window", { location });
  return location;
}

function answering(body: unknown) {
  return vi.fn(async () => Response.json(body));
}

const REFUSED = { type: "error", error: { status: 401, message: "Unauthorized" } };

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

describe("createRemoteSignInFetch", () => {
  beforeEach(() => {
    authInterceptorState.reset();
    vi.unstubAllGlobals();
  });

  it("sends a person whose command is refused with 401 to sign in, keeping the page they were on", async () => {
    const location = on("rhys.nocturne.run", "/settings/account", "?tab=passkeys");

    const response = await createRemoteSignInFetch(answering(REFUSED))("/_app/remote/abc123/add", { method: "POST" });

    expect(location.href).toBe(`/auth/login?returnUrl=${encodeURIComponent("/settings/account?tab=passkeys")}`);
    expect(await response.json()).toEqual(REFUSED);
  });

  it("leaves a share-host viewer on the page when a command is refused with 401", async () => {
    const location = on("k7m2q9x4r3wt.share.nocturne.run", "/", "");

    await createRemoteSignInFetch(answering(REFUSED))("/_app/remote/abc123/add", { method: "POST" });

    expect(location.href).toBe("https://k7m2q9x4r3wt.share.nocturne.run/");
  });

  it("leaves a sign-in or recovery page to answer its own refusal", async () => {
    const location = on("rhys.nocturne.run", "/auth/recovery/passkey", "?username=sam");

    await createRemoteSignInFetch(answering(REFUSED))("/_app/remote/abc123/registerComplete", { method: "POST" });

    expect(location.href).toBe("https://rhys.nocturne.run/auth/recovery/passkey?username=sam");
  });

  it("sends a person linking a bot account to sign in when their session has ended", async () => {
    const location = on("rhys.nocturne.run", "/auth/bot/authorize", "?token=abc");

    await createRemoteSignInFetch(answering(REFUSED))("/_app/remote/abc123/claimLink", { method: "POST" });

    expect(location.href).toBe(`/auth/login?returnUrl=${encodeURIComponent("/auth/bot/authorize?token=abc")}`);
  });

  it("stays on the page for a command refused with another status", async () => {
    const location = on("rhys.nocturne.run", "/calendar", "");

    await createRemoteSignInFetch(answering({ type: "error", error: { status: 403, message: "Forbidden" } }))(
      "/_app/remote/abc123/add",
      { method: "POST" },
    );

    expect(location.href).toBe("https://rhys.nocturne.run/calendar");
  });

  it("leaves a query and any other request to their own handling", async () => {
    const location = on("rhys.nocturne.run", "/calendar", "");

    await createRemoteSignInFetch(answering(REFUSED))("/_app/remote/abc123/get?payload=x");
    await createRemoteSignInFetch(answering(REFUSED))("/api/otel/errors", { method: "POST" });

    expect(location.href).toBe("https://rhys.nocturne.run/calendar");
  });
});
