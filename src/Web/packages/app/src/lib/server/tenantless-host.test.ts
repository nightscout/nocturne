import { describe, expect, it } from "vitest";
import {
  classifyHost,
  isTenantlessHost,
  parseDashboardSlugs,
} from "./tenantless-host";

const BASE = "nocturne.run";

describe("parseDashboardSlugs", () => {
  it("reserves nothing when unset, so reserving a slug stays opt-in", () => {
    expect(parseDashboardSlugs(undefined)).toEqual([]);
    expect(parseDashboardSlugs(null)).toEqual([]);
  });

  it("reserves nothing when explicitly empty", () => {
    expect(parseDashboardSlugs("")).toEqual([]);
    expect(parseDashboardSlugs("  ")).toEqual([]);
  });

  it("splits, trims, and lowercases", () => {
    expect(parseDashboardSlugs(" Home , Portal ")).toEqual(["home", "portal"]);
  });
});

describe("classifyHost", () => {
  it("classifies the apex as the apex, leaving whether it serves a tenant to the API", () => {
    expect(classifyHost(BASE, BASE)).toEqual({ kind: "apex", slug: null });
  });

  it("ignores ports on both sides", () => {
    expect(classifyHost("nocturne.run:1612", "nocturne.run")).toEqual({
      kind: "apex",
      slug: null,
    });
    expect(classifyHost("acme.nocturne.run:1612", "nocturne.run:1612")).toEqual({
      kind: "tenant",
      slug: "acme",
    });
  });

  it("classifies a tenant subdomain, preserving slug casing", () => {
    expect(classifyHost("AcMe.nocturne.run", BASE)).toEqual({ kind: "tenant", slug: "AcMe" });
  });

  it("classifies reserved dashboard slugs as dashboard slugs", () => {
    const reserved = ["dashboard", "app"];
    expect(classifyHost("dashboard.nocturne.run", BASE, reserved).kind).toBe("dashboard-slug");
    expect(classifyHost("app.nocturne.run", BASE, reserved).kind).toBe("dashboard-slug");
    expect(classifyHost("DASHBOARD.nocturne.run", BASE, reserved).kind).toBe("dashboard-slug");
  });

  it("treats an unreserved slug as a tenant, which is every slug by default", () => {
    expect(classifyHost("app.nocturne.run", BASE, ["dashboard"])).toEqual({
      kind: "tenant",
      slug: "app",
    });
    expect(classifyHost("dashboard.nocturne.run", BASE)).toEqual({
      kind: "tenant",
      slug: "dashboard",
    });
  });

  it("classifies a share host as share, never as tenant or dashboard", () => {
    expect(classifyHost("k7m2q9x4r3wt.share.nocturne.run", BASE)).toEqual({
      kind: "share",
      slug: null,
    });
    // A share token that happens to spell a reserved slug is still a share host.
    expect(classifyHost("dashboard.share.nocturne.run", BASE).kind).toBe("share");
  });

  it("classifies unrelated hosts and missing config as unknown", () => {
    expect(classifyHost("evil.com", BASE).kind).toBe("unknown");
    // Suffix match, not substring: a host that merely ends with the base domain's text.
    expect(classifyHost("notnocturne.run", BASE).kind).toBe("unknown");
    expect(classifyHost(BASE, null).kind).toBe("unknown");
    expect(classifyHost(null, BASE).kind).toBe("unknown");
  });

  it("is suffix-based, not label-counting, for multi-label base domains", () => {
    const deep = "nocturne.example.com";
    expect(classifyHost(deep, deep).kind).toBe("apex");
    expect(classifyHost("dashboard.nocturne.example.com", deep, ["dashboard"]).kind).toBe(
      "dashboard-slug"
    );
    expect(classifyHost("acme.nocturne.example.com", deep)).toEqual({
      kind: "tenant",
      slug: "acme",
    });
    // A three-label host under a two-label base is a tenant, not an apex.
    expect(classifyHost("acme.example.com", "example.com")).toEqual({
      kind: "tenant",
      slug: "acme",
    });
  });
});

describe("isTenantlessHost", () => {
  it("serves the full app on an apex that resolves a sole tenant", () => {
    // The single-tenant self-hosted install: nocturne.example.com IS the app. Calling it
    // tenantless trims its sidebar and bounces it to a wildcard subdomain that, per the
    // deployment's TLS shape, may not resolve or may present an invalid certificate.
    expect(isTenantlessHost("apex", true)).toBe(false);
  });

  it("serves the dashboard on an apex that resolves nothing", () => {
    // Zero tenants, or several: either way the apex names no tenant of its own.
    expect(isTenantlessHost("apex", false)).toBe(true);
  });

  it("serves the dashboard on a reserved slug regardless of what the apex resolves", () => {
    expect(isTenantlessHost("dashboard-slug", true)).toBe(true);
    expect(isTenantlessHost("dashboard-slug", false)).toBe(true);
  });

  it("never serves the dashboard on a tenant, share, or unrelated host", () => {
    for (const resolved of [true, false]) {
      expect(isTenantlessHost("tenant", resolved)).toBe(false);
      expect(isTenantlessHost("share", resolved)).toBe(false);
      expect(isTenantlessHost("unknown", resolved)).toBe(false);
    }
  });
});
