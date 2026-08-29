import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";

// The sidebar's own remote calls; neither says anything about which entries it offers.
vi.mock("$api/user-preferences.remote", () => ({
  updateLanguagePreference: () => Promise.resolve(),
}));
vi.mock("$lib/api/generated/myTenants.generated.remote", () => ({
  getMyTenants: () => ({ current: [] }),
}));

import Harness from "./AppSidebarHarness.test.svelte";

const link = (name: string) => page.getByRole("link", { name, exact: true });
const group = (name: string) => page.getByRole("button", { name, exact: true });

/** A signed-in member, as the layout passes one down. */
const MEMBER = { subjectId: "s1", name: "Sam", roles: [], permissions: [] };

describe("AppSidebar", () => {
  beforeEach(async () => {
    // Below the sidebar's mobile breakpoint it renders as a closed sheet and offers nothing.
    await page.viewport(1280, 900);
    pageState.data = {};
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
});
