import { describe, it, expect } from "vitest";
import {
  resolveCookieDomain,
  resolveSingleTenantLanding,
  resolveTenantSwitcher,
  tenantUrl,
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

  it("does not redirect to a sole tenant that is inactive", () => {
    expect(
      resolveSingleTenantLanding(
        [{ slug: "alice", isActive: false }],
        "example.com",
        "https:"
      )
    ).toBeNull();
  });

  it("redirects to the sole active tenant among inactive ones", () => {
    expect(
      resolveSingleTenantLanding(
        [
          { slug: "alice", isActive: false },
          { slug: "bob", isActive: true },
          { slug: "carol", isActive: false },
        ],
        "example.com",
        "https:"
      )
    ).toBe("https://bob.example.com/");
  });

  it("renders the dashboard when every tenant is inactive", () => {
    expect(
      resolveSingleTenantLanding(
        [
          { slug: "alice", isActive: false },
          { slug: "bob", isActive: false },
        ],
        "example.com",
        "https:"
      )
    ).toBeNull();
  });

  it("does not redirect when several tenants are active", () => {
    expect(
      resolveSingleTenantLanding(
        [
          { slug: "alice", isActive: true },
          { slug: "bob", isActive: true },
          { slug: "carol", isActive: false },
        ],
        "example.com",
        "https:"
      )
    ).toBeNull();
  });

  it("treats an absent isActive as active", () => {
    // Every property of the generated TenantDto is optional, and the rest of the app reads this
    // field as `isActive ?? true`.
    expect(
      resolveSingleTenantLanding(
        [{ slug: "alice", isActive: undefined }],
        "example.com",
        "https:"
      )
    ).toBe("https://alice.example.com/");
  });
});

describe("resolveTenantSwitcher", () => {
  const alice = { id: "a", slug: "alice", displayName: "Alice" };
  const bob = { id: "b", slug: "bob", displayName: "Bob" };

  it("offers the other tenants a visitor belongs to", () => {
    const switcher = resolveTenantSwitcher([alice, bob], "alice");

    expect(switcher.targets).toEqual([
      { id: "b", slug: "bob", displayName: "Bob" },
    ]);
    expect(switcher.totalCount).toBe(2);
  });

  it("offers every tenant on a tenantless host", () => {
    const switcher = resolveTenantSwitcher([alice, bob], null);

    expect(switcher.targets.map((t) => t.slug)).toEqual(["alice", "bob"]);
  });

  it("never offers an inactive tenant as a switch target", () => {
    const switcher = resolveTenantSwitcher(
      [
        { ...alice, isActive: true },
        { ...bob, isActive: false },
      ],
      null
    );

    expect(switcher.targets.map((t) => t.slug)).toEqual(["alice"]);
  });

  it("does not count inactive tenants, so one active tenant shows no switcher", () => {
    // totalCount > 1 is what renders the switcher and the Tenants nav entry.
    const switcher = resolveTenantSwitcher(
      [
        { ...alice, isActive: true },
        { ...bob, isActive: false },
      ],
      "alice"
    );

    expect(switcher.totalCount).toBe(1);
    expect(switcher.targets).toEqual([]);
  });

  it("treats an absent isActive as active", () => {
    const switcher = resolveTenantSwitcher(
      [
        { ...alice, isActive: undefined },
        { ...bob, isActive: undefined },
      ],
      null
    );

    expect(switcher.targets.map((t) => t.slug)).toEqual(["alice", "bob"]);
    expect(switcher.totalCount).toBe(2);
  });

  it("drops tenants with no id or no slug", () => {
    const switcher = resolveTenantSwitcher(
      [{ id: "x" }, { slug: "ghost" }, bob],
      null
    );

    expect(switcher.targets.map((t) => t.slug)).toEqual(["bob"]);
  });

  it("has nothing to switch between with no tenants", () => {
    for (const tenants of [[], null, undefined]) {
      const switcher = resolveTenantSwitcher(tenants, null);
      expect(switcher.targets).toEqual([]);
      expect(switcher.totalCount).toBe(0);
    }
  });

  it("carries a missing display name as null", () => {
    const switcher = resolveTenantSwitcher([{ id: "c", slug: "carol" }], null);

    expect(switcher.targets).toEqual([
      { id: "c", slug: "carol", displayName: null },
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
    // Browsers reject a Domain attribute on "localhost" outright.
    expect(resolveCookieDomain("localhost")).toBeNull();
    expect(resolveCookieDomain("localhost:1612")).toBeNull();
  });

  it("keeps a .localhost host cookie host-only", () => {
    // Chromium does not reliably scope cookies across *.localhost names.
    expect(resolveCookieDomain("nocturne.localhost")).toBeNull();
    expect(resolveCookieDomain("nocturne.localhost:1612")).toBeNull();
    expect(resolveCookieDomain("NOCTURNE.LOCALHOST")).toBeNull();
  });

  it("keeps an IP-literal host cookie host-only", () => {
    // A browser discards any cookie whose Domain is set on an IP host, losing the value.
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
