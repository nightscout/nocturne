import { describe, it, expect } from "vitest";
import { resolveSingleTenantLanding, tenantUrl } from "./tenant-host";

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
        [{ slug: "alice" }, { slug: null }],
        "example.com",
        "https:"
      )
    ).toBe("https://alice.example.com/");
    expect(
      resolveSingleTenantLanding([{ slug: null }], "example.com", "https:")
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
