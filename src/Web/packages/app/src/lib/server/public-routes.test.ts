import { describe, it, expect } from "vitest";
import {
  isPublicRoute,
  PUBLIC_PREFIXES,
  requiresSignIn,
  statusProbeRedirect,
} from "./public-routes";
import { SHARE_UNAVAILABLE_PATH } from "$lib/share-host";

describe("isPublicRoute", () => {
  it("treats /join as public so an invitee without an account can accept an invite", () => {
    // MemberInviteService emits `{baseUrl}/join?token={token}`.
    expect(isPublicRoute("/join")).toBe(true);
    expect(isPublicRoute("/join?token=abc")).toBe(true);
  });

  it("does not carry a prefix for the removed /invite route", () => {
    expect(PUBLIC_PREFIXES).not.toContain("/invite");
  });

  it("treats the other unauthenticated destinations as public", () => {
    for (const path of [
      "/",
      "/auth/login",
      "/setup",
      "/clock/abc",
      "/guest/CODE",
      "/terms",
      "/privacy",
      "/api/v4/status",
    ]) {
      expect(isPublicRoute(path), path).toBe(true);
    }
  });

  it("treats static assets as public", () => {
    expect(isPublicRoute("/_app/immutable/chunk.js")).toBe(true);
    expect(isPublicRoute("/assets/logo.svg")).toBe(true);
    expect(isPublicRoute("/favicon.ico")).toBe(true);
  });

  it("keeps app routes behind requireAuthentication", () => {
    for (const path of [
      "/settings/account",
      "/reports/agp",
      "/alerts",
      "/oauth/device",
      "/calendar",
    ]) {
      expect(isPublicRoute(path), path).toBe(false);
    }
  });
});

describe("requiresSignIn", () => {
  /** A locked-down tenant with an anonymous visitor on a tenant-scoped route. */
  const lockedDown = {
    pathname: "/reports/agp",
    requireAuthentication: true,
    isAuthenticated: false,
    isShareHost: false,
  };

  it("sends an anonymous visitor on a locked-down tenant host to sign in", () => {
    expect(requiresSignIn(lockedDown)).toBe(true);
  });

  it("exempts the share host, whose visitors can never be authenticated", () => {
    expect(requiresSignIn({ ...lockedDown, isShareHost: true })).toBe(false);
  });

  it("exempts the share host on the data requests the client router issues", () => {
    // Not a public route by path, and the one the shared dashboard itself is re-fetched through.
    expect(
      requiresSignIn({ ...lockedDown, pathname: "/__data.json", isShareHost: true })
    ).toBe(false);
  });

  it("leaves a signed-in visitor and an unlocked tenant alone", () => {
    expect(requiresSignIn({ ...lockedDown, isAuthenticated: true })).toBe(false);
    expect(requiresSignIn({ ...lockedDown, requireAuthentication: false })).toBe(false);
  });

  it("leaves the public routes alone on a locked-down tenant host", () => {
    expect(requiresSignIn({ ...lockedDown, pathname: "/" })).toBe(false);
    expect(requiresSignIn({ ...lockedDown, pathname: "/auth/login" })).toBe(false);
  });
});

describe("statusProbeRedirect", () => {
  /** A self-hosted install with no marketing site, on an ordinary tenant host. */
  const selfHosted = {
    isShareHost: false,
    recoveryMode: false,
    marketingUrl: undefined,
  };

  it("claims every status a share host cannot act on, ahead of the instance-wide ones", () => {
    // 404 an unresolvable token, 403 a suspended tenant, 503 an API that is itself unready. Each
    // would otherwise steer to /setup, /auth/recovery or the marketing site, none of which a
    // share host can do anything with.
    for (const apiStatus of [404, 403, 503]) {
      expect(
        statusProbeRedirect({
          ...selfHosted,
          isShareHost: true,
          recoveryMode: true,
          marketingUrl: "https://nocturne.run",
          apiStatus,
        }),
        String(apiStatus)
      ).toEqual({ location: SHARE_UNAVAILABLE_PATH, status: 303 });
    }
  });

  it("leaves every other host on its own destinations", () => {
    expect(statusProbeRedirect({ ...selfHosted, apiStatus: 503 })).toEqual({
      location: "/setup",
      status: 303,
    });
    expect(
      statusProbeRedirect({ ...selfHosted, apiStatus: 503, recoveryMode: true })
    ).toEqual({ location: "/auth/recovery", status: 303 });
    expect(statusProbeRedirect({ ...selfHosted, apiStatus: 404 })).toEqual({
      location: "/setup",
      status: 303,
    });
    expect(
      statusProbeRedirect({ ...selfHosted, apiStatus: 404, marketingUrl: "https://nocturne.run" })
    ).toEqual({ location: "https://nocturne.run", status: 302 });
  });

  it("sends nowhere on a status neither branch answers for", () => {
    // 403 is among them: only a share host reads it as a suspended tenant.
    for (const apiStatus of [401, 403, 500, undefined, null, "404"]) {
      expect(statusProbeRedirect({ ...selfHosted, apiStatus }), String(apiStatus)).toBeNull();
    }
  });

  it("sends a share host nowhere on a status that is not its dead end either", () => {
    for (const apiStatus of [401, 500, undefined, "404"]) {
      expect(
        statusProbeRedirect({ ...selfHosted, isShareHost: true, apiStatus }),
        String(apiStatus)
      ).toBeNull();
    }
  });
});
