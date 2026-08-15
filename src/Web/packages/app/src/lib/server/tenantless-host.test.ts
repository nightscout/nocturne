import { describe, expect, it } from "vitest";
import {
  DEFAULT_DASHBOARD_SLUGS,
  classifyHost,
  isTenantlessHost,
  parseDashboardSlugs,
} from "./tenantless-host";

const BASE = "nocturne.run";

describe("parseDashboardSlugs", () => {
  it("takes the defaults when unset", () => {
    expect(parseDashboardSlugs(undefined)).toEqual(DEFAULT_DASHBOARD_SLUGS);
    expect(parseDashboardSlugs(null)).toEqual(DEFAULT_DASHBOARD_SLUGS);
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
  it("classifies the apex as tenantless", () => {
    expect(classifyHost(BASE, BASE)).toEqual({ kind: "tenantless", slug: null });
  });

  it("ignores ports on both sides", () => {
    expect(classifyHost("nocturne.run:1612", "nocturne.run")).toEqual({
      kind: "tenantless",
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

  it("classifies reserved dashboard slugs as tenantless", () => {
    expect(classifyHost("dashboard.nocturne.run", BASE).kind).toBe("tenantless");
    expect(classifyHost("app.nocturne.run", BASE).kind).toBe("tenantless");
    expect(classifyHost("DASHBOARD.nocturne.run", BASE).kind).toBe("tenantless");
  });

  it("treats a reserved slug as a tenant once it is no longer reserved", () => {
    expect(classifyHost("app.nocturne.run", BASE, ["dashboard"])).toEqual({
      kind: "tenant",
      slug: "app",
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
    expect(classifyHost(deep, deep).kind).toBe("tenantless");
    expect(classifyHost("dashboard.nocturne.example.com", deep).kind).toBe("tenantless");
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
  it("is true only for the apex and reserved slugs", () => {
    expect(isTenantlessHost(BASE, BASE)).toBe(true);
    expect(isTenantlessHost("dashboard.nocturne.run", BASE)).toBe(true);
    expect(isTenantlessHost("acme.nocturne.run", BASE)).toBe(false);
    expect(isTenantlessHost("tok.share.nocturne.run", BASE)).toBe(false);
    expect(isTenantlessHost("evil.com", BASE)).toBe(false);
  });
});
