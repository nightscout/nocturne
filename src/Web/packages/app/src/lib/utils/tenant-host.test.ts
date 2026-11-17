import { describe, it, expect } from "vitest";
import {
  resolveCookieDomain,
  resolveSingleTenantLanding,
  tenantUrl,
  toTenantSwitcherTargets,
} from "./tenant-host";

describe("tenantUrl", () => {
  it("builds a tenant subdomain URL", () => {
    expect(tenantUrl("alice", "example.com", "https:")).toBe(
      "https://alice.example.com/"
    );
  });

  it("keeps a port already embedded in the base domain", () => {
    expect(tenantUrl("alice", "nocturne.localhost:1612", "https:")).toBe(
      "https://alice.nocturne.localhost:1612/"
    );
  });

  it("does not add a port of its own", () => {
    expect(tenantUrl("alice", "example.com", "http:")).toBe(
      "http://alice.example.com/"
    );
  });

  it("supports multi-label base domains", () => {
    expect(tenantUrl("alice", "cgm.example.co.uk", "https:")).toBe(
      "https://alice.cgm.example.co.uk/"
    );
  });
});

describe("resolveSingleTenantLanding", () => {
  it("sends a caregiver with exactly one tenant straight to it", () => {
    expect(
      resolveSingleTenantLanding([{ slug: "alice" }], "example.com", "https:")
    ).toBe("https://alice.example.com/");
  });

  it("renders the dashboard for several tenants", () => {
    expect(
      resolveSingleTenantLanding(
        [{ slug: "alice" }, { slug: "bob" }],
        "example.com",
        "https:"
      )
    ).toBeNull();
  });

  it("renders the dashboard when there are no tenants", () => {
    expect(resolveSingleTenantLanding([], "example.com", "https:")).toBeNull();
    expect(resolveSingleTenantLanding(null, "example.com", "https:")).toBeNull();
    expect(
      resolveSingleTenantLanding(undefined, "example.com", "https:")
    ).toBeNull();
  });

  it("ignores tenants with no slug, and counts what remains", () => {
    expect(
      resolveSingleTenantLanding(
        [{ slug: "alice" }, {}],
        "example.com",
        "https:"
      )
    ).toBe("https://alice.example.com/");
    expect(
      resolveSingleTenantLanding([{}], "example.com", "https:")
    ).toBeNull();
  });

  it("cannot build a URL without a base domain", () => {
    expect(resolveSingleTenantLanding([{ slug: "alice" }], null, "https:")).toBeNull();
    expect(resolveSingleTenantLanding([{ slug: "alice" }], "", "https:")).toBeNull();
  });

  it("does not redirect to a sole tenant whose slug is itself a dashboard slug", () => {
    // That tenant's host IS the dashboard host, so the redirect would land back on this same
    // load and redirect again, forever. Nothing is reserved by default, so this only arises once
    // an operator sets DASHBOARD_SLUGS — and it may name a slug some tenant already holds.
    expect(
      resolveSingleTenantLanding([{ slug: "home" }], "example.com", "https:", ["home"])
    ).toBeNull();
    expect(
      resolveSingleTenantLanding([{ slug: "HOME" }], "example.com", "https:", ["home"])
    ).toBeNull();
  });

  it("still redirects to a sole tenant that is not a dashboard slug", () => {
    expect(
      resolveSingleTenantLanding([{ slug: "alice" }], "example.com", "https:", [
        "dashboard",
        "app",
      ])
    ).toBe("https://alice.example.com/");
  });
});

describe("toTenantSwitcherTargets", () => {
  const alice = { id: "a", slug: "alice", displayName: "Alice" };
  const bob = { id: "b", slug: "bob", displayName: "Bob" };

  it("offers every tenant given, the one on screen included", () => {
    expect(toTenantSwitcherTargets([alice, bob])).toEqual([
      { slug: "alice", displayName: "Alice" },
      { slug: "bob", displayName: "Bob" },
    ]);
  });

  it("drops a tenant with no slug, having no host to send anyone to", () => {
    expect(toTenantSwitcherTargets([{ id: "x" }, bob]).map((t) => t.slug)).toEqual(["bob"]);
  });

  it("has nothing to switch between with no tenants", () => {
    expect(toTenantSwitcherTargets([])).toEqual([]);
  });

  it("carries a missing display name as null", () => {
    expect(toTenantSwitcherTargets([{ id: "c", slug: "carol" }])).toEqual([
      { slug: "carol", displayName: null },
    ]);
  });
});

describe("resolveCookieDomain", () => {
  it("widens to every host under the base domain", () => {
    expect(resolveCookieDomain("example.com")).toBe(".example.com");
  });

  it("widens a multi-label base domain to itself, not to its parent", () => {
    // nocturne.example.com is as valid a base domain as example.com; widening to .example.com
    // would hand the cookie to hosts of an unrelated deployment.
    expect(resolveCookieDomain("nocturne.example.com")).toBe(".nocturne.example.com");
  });

  it("drops the port, which a Domain attribute cannot carry", () => {
    expect(resolveCookieDomain("example.com:1612")).toBe(".example.com");
  });

  it("keeps a single-label host cookie host-only", () => {
    expect(resolveCookieDomain("localhost")).toBeNull();
    expect(resolveCookieDomain("localhost:1612")).toBeNull();
  });

  it("keeps a .localhost host cookie host-only", () => {
    expect(resolveCookieDomain("nocturne.localhost")).toBeNull();
    expect(resolveCookieDomain("nocturne.localhost:1612")).toBeNull();
    expect(resolveCookieDomain("NOCTURNE.LOCALHOST")).toBeNull();
  });

  it("keeps an IP-literal host cookie host-only", () => {
    expect(resolveCookieDomain("192.168.1.10")).toBeNull();
    expect(resolveCookieDomain("192.168.1.10:1612")).toBeNull();
    expect(resolveCookieDomain("[::1]:1612")).toBeNull();
  });

  it("has no domain to widen to without a base domain", () => {
    expect(resolveCookieDomain(null)).toBeNull();
    expect(resolveCookieDomain(undefined)).toBeNull();
    expect(resolveCookieDomain("")).toBeNull();
    expect(resolveCookieDomain(":1612")).toBeNull();
  });
});
