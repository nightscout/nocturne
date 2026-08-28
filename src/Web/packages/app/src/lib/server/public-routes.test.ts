import { describe, it, expect } from "vitest";
import { isPublicRoute, PUBLIC_PREFIXES, requiresSignIn } from "./public-routes";

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
