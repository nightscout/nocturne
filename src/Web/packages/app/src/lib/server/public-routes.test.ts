import { describe, it, expect } from "vitest";
import { isPublicRoute, PUBLIC_PREFIXES } from "./public-routes";

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
