import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";

// The sidebar's own remote calls; neither says anything about which nav entries it offers.
const membership: { tenants: unknown[] } = vi.hoisted(() => ({ tenants: [] }));
vi.mock("$api/user-preferences.remote", () => ({
  updateLanguagePreference: () => Promise.resolve(),
}));
vi.mock("$lib/api/generated/myTenants.generated.remote", () => ({
  getMyTenants: () => ({ current: membership.tenants }),
}));

// Switching tenant is a document navigation, which would take the test runner with it.
const visited: { urls: string[] } = vi.hoisted(() => ({ urls: [] }));
vi.mock("$lib/utils/tenant-host", async (importOriginal) => {
  const actual =
    await importOriginal<typeof import("$lib/utils/tenant-host")>();
  return {
    ...actual,
    goToTenant: (slug: string, baseDomain: string) => {
      visited.urls.push(actual.tenantUrl(slug, baseDomain, "https:"));
    },
  };
});

import Harness from "./AppSidebarHarness.test.svelte";

const link = (name: string) => page.getByRole("link", { name, exact: true });
const group = (name: string) => page.getByRole("button", { name, exact: true });
const option = (name: string) => page.getByRole("option", { name, exact: true });

/** A signed-in member, as the layout passes one down. */
const MEMBER = { subjectId: "s1", name: "Sam", roles: [], permissions: [] };

const TENANTS = [
  { id: "t1", slug: "alpha", displayName: "Alpha", isActive: true },
  { id: "t2", slug: "bravo", displayName: "Bravo", isActive: true },
  { id: "t3", slug: "charlie", displayName: "Charlie", isActive: true },
];

/** The sidebar as a member of every tenant above sees it, viewing one that is not the first. */
function renderViewingBravo() {
  pageState.data = { effectivePermissions: ["*"] };
  membership.tenants = TENANTS;

  render(Harness, {
    user: MEMBER,
    currentSlug: "bravo",
    baseDomain: "example.com",
  });
}

describe("AppSidebar", () => {
  beforeEach(async () => {
    // Below the sidebar's mobile breakpoint it renders as a closed sheet and offers nothing.
    await page.viewport(1280, 900);
    pageState.data = {};
    membership.tenants = [];
    visited.urls = [];
  });

  it("offers a public share the dashboard and a way to sign in, and nothing the owner acts on", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };

    render(Harness, {});

    await expect.element(link("Dashboard")).toBeVisible();
    await expect.element(link("Sign in")).toBeVisible();
    await expect.element(group("Reports")).not.toBeInTheDocument();
    await expect.element(link("Food")).not.toBeInTheDocument();
    await expect.element(group("Settings")).not.toBeInTheDocument();
  });

  it("offers a public share its reports when the share grants them", async () => {
    pageState.data = { effectivePermissions: ["glucose.read", "reports.read"] };

    render(Harness, {});

    await expect.element(group("Reports")).toBeVisible();
    await expect.element(group("Settings")).not.toBeInTheDocument();
  });

  it("offers a member the surfaces they own", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(Harness, { user: MEMBER });

    await expect.element(group("Settings")).toBeVisible();
    await expect.element(link("Food")).toBeVisible();
    await expect.element(group("Reports")).toBeVisible();
  });

  it("names the tenant on screen, not the first one the member belongs to", async () => {
    renderViewingBravo();

    await expect.element(group("Bravo (bravo)")).toBeVisible();
  });

  it("holds the tenant on screen as the switcher's selection", async () => {
    renderViewingBravo();

    await group("Bravo (bravo)").click();

    await expect
      .element(option("Bravo (bravo)"))
      .toHaveAttribute("aria-selected", "true");
    await expect
      .element(option("Alpha (alpha)"))
      .not.toHaveAttribute("aria-selected");
  });

  it("goes to the host of the tenant the visitor picks", async () => {
    renderViewingBravo();

    await group("Bravo (bravo)").click();
    await option("Charlie (charlie)").click();
    expect(visited.urls).toEqual(["https://charlie.example.com/"]);
  });
});
