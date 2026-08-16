import { describe, expect, it } from "vitest";
import { filterTenantlessNav, isTenantlessRoute } from "./tenantless-navigation";

describe("filterTenantlessNav", () => {
  it("keeps the cross-tenant dashboard and drops tenant-scoped pages", () => {
    const items = [
      { title: "Dashboard", href: "/" },
      { title: "Calendar", href: "/calendar" },
      { title: "Clock", href: "/clock" },
      { title: "Tenants", href: "/tenants" },
    ];

    expect(filterTenantlessNav(items)).toEqual([{ title: "Dashboard", href: "/" }]);
  });

  it("drops the settings group, whose pages all need a resolved tenant", () => {
    // Account and appearance look subject-scoped but call /api/auth/passkey/*, /api/v4/totp/*,
    // and /api/v4/settings, none of which the API serves without a tenant. Listing them would
    // put entries in the sidebar that 404 when opened.
    const items = [
      {
        title: "Settings",
        children: [
          { title: "Setup", href: "/setup" },
          { title: "Account", href: "/settings/account" },
          { title: "Therapy", href: "/settings/profile" },
          { title: "Appearance", href: "/settings/appearance" },
          { title: "Sharing & Privacy", href: "/settings/members" },
        ],
      },
    ];

    expect(filterTenantlessNav(items)).toEqual([]);
  });

  it("drops a group whose children are all tenant-scoped, leaving no empty heading", () => {
    const items = [
      {
        title: "Alerts",
        children: [
          { title: "Rules", href: "/alerts" },
          { title: "History", href: "/alerts/history" },
        ],
      },
    ];

    expect(filterTenantlessNav(items)).toEqual([]);
  });

  it("does not mutate the input", () => {
    const items = [
      { title: "Settings", children: [{ title: "Setup", href: "/setup" }] },
    ];

    filterTenantlessNav(items);

    expect(items[0]!.children).toHaveLength(1);
  });

  it("drops an item that carries neither an href nor children", () => {
    const items = [{ title: "Separator", href: undefined }];

    expect(filterTenantlessNav(items)).toEqual([]);
  });
});

describe("isTenantlessRoute", () => {
  it("rejects tenant-scoped routes", () => {
    expect(isTenantlessRoute("/calendar")).toBe(false);
    expect(isTenantlessRoute("/tenants")).toBe(false);
    expect(isTenantlessRoute("/settings/members")).toBe(false);
    expect(isTenantlessRoute("/settings/account")).toBe(false);
    expect(isTenantlessRoute("/settings/appearance")).toBe(false);
  });

  it("matches whole paths, not prefixes", () => {
    // "/" must not admit every path.
    expect(isTenantlessRoute("/reports")).toBe(false);
  });
});
