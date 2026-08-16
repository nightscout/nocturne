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

  it("keeps only the subject-scoped page in the settings group", () => {
    // Appearance carries the subject's own units/formats/theme. Account looks subject-scoped and
    // its data is, but /api/auth/passkey/* and /api/auth/totp/* are not served off a tenant, so
    // listing it would put an entry in the sidebar that 404s when opened.
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

    expect(filterTenantlessNav(items)).toEqual([
      {
        title: "Settings",
        children: [{ title: "Appearance", href: "/settings/appearance" }],
      },
    ]);
  });

  it("admits the subject's own appearance page as a route", () => {
    expect(isTenantlessRoute("/settings/appearance")).toBe(true);
  });

  it("keeps the rest of settings off a tenantless host", () => {
    // The tenant-scoped pages would render a shell and then 404, and account needs auth
    // endpoints the API does not serve without a tenant.
    for (const href of [
      "/settings",
      "/settings/account",
      "/settings/members",
      "/settings/profile",
      "/settings/trackers",
    ]) {
      expect(isTenantlessRoute(href)).toBe(false);
    }
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
  });

  it("matches whole paths, not prefixes", () => {
    // "/" must not admit every path.
    expect(isTenantlessRoute("/reports")).toBe(false);
  });
});
