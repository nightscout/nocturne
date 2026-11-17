import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";
import { createQueryResource, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { flushIdle, resetIdle } from "#lib/test-stubs/when-idle.js";

vi.mock("#lib/utils/when-idle.js", () => import("#lib/test-stubs/when-idle.js"));

// The sidebar's own remote calls; none says anything about which nav entries it offers.
const getDnd = vi.hoisted(() => vi.fn());
const updateDnd = vi.hoisted(() => vi.fn());
vi.mock("#lib/api/user-preferences.remote.js", () => ({
  updateLanguagePreference: () => Promise.resolve(),
}));
vi.mock("#lib/api/generated/tenantAlertSettings.generated.remote.js", () => ({
  get: () => getDnd(),
  update: (arg: unknown) => updateDnd(arg),
}));

// Switching tenant is a document navigation, which would take the test runner with it.
const visited: { urls: string[] } = vi.hoisted(() => ({ urls: [] }));
vi.mock("#lib/utils/tenant-host.js", async (importOriginal) => {
  const actual =
    await importOriginal<typeof import("#lib/utils/tenant-host.js")>();
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
  { slug: "alpha", displayName: "Alpha" },
  { slug: "bravo", displayName: "Bravo" },
  { slug: "charlie", displayName: "Charlie" },
];

/** The sidebar as a member of every tenant above sees it, viewing one that is not the first. */
function renderViewingBravo() {
  pageState.data = { effectivePermissions: ["*"] };
  render(Harness, {
    user: MEMBER,
    currentSlug: "bravo",
    baseDomain: "example.com",
    tenantTargets: TENANTS,
  });
}

describe("AppSidebar", () => {
  beforeEach(async () => {
    // Below the sidebar's mobile breakpoint it renders as a closed sheet and offers nothing.
    await page.viewport(1280, 900);
    pageState.data = {};
    (pageState.url as URL).pathname = "/";
    visited.urls = [];
    resetIdle();
    getDnd.mockReset();
    getDnd.mockImplementation(() => remoteQuery(() => ({ dndManualActive: false })));
    updateDnd.mockReset();
    updateDnd.mockResolvedValue({ dndManualActive: true });
  });

  it("offers a public share the dashboard and a way to sign in, and nothing the owner acts on", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };

    render(Harness, {});

    await expect.element(link("Dashboard")).toBeVisible();
    await expect.element(link("Sign in")).toBeVisible();
    await expect.element(group("Reports")).not.toBeInTheDocument();
    await expect.element(link("Food")).not.toBeInTheDocument();
    await expect.element(group("My settings")).not.toBeInTheDocument();
  });

  it("offers a public share its reports when the share grants them", async () => {
    pageState.data = { effectivePermissions: ["glucose.read", "reports.read"] };

    render(Harness, {});

    await expect.element(group("Reports")).toBeVisible();
    await expect.element(group("My settings")).not.toBeInTheDocument();
  });

  it("offers a member the surfaces they own", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(Harness, { user: MEMBER });

    await expect.element(group("My settings")).toBeVisible();
    await expect.element(link("Food")).toBeVisible();
    await expect.element(group("Reports")).toBeVisible();
  });

  it("names the tenant on screen by its name and slug, before the switcher opens", async () => {
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

  it("lists every tenant the moment the switcher opens", async () => {
    renderViewingBravo();

    await group("Bravo (bravo)").click();

    await expect.element(option("Alpha (alpha)")).toBeVisible();
    await expect.element(option("Charlie (charlie)")).toBeVisible();
  });

  it("goes to the host of the tenant the visitor picks", async () => {
    renderViewingBravo();

    await group("Bravo (bravo)").click();
    await option("Charlie (charlie)").click();
    expect(visited.urls).toEqual(["https://charlie.example.com/"]);
  });

  it("offers the member's own data on the apex host, where no tenant is on screen", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(Harness, { user: MEMBER, baseDomain: "example.com", tenantTargets: TENANTS });

    await group("My data").click();

    await expect.element(option("My data")).toHaveAttribute("aria-selected", "true");
    await expect.element(option("Alpha (alpha)")).toBeVisible();
  });

  it("shows a multi-tenant member the switcher and the Tenants item", async () => {
    renderViewingBravo();

    await expect.element(page.getByText("Viewing data for")).toBeVisible();
    await expect.element(link("Tenants")).toBeVisible();
  });

  it("offers a single-tenant member no switcher and no Tenants item", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(Harness, {
      user: MEMBER,
      currentSlug: "alpha",
      baseDomain: "example.com",
      tenantTargets: TENANTS.slice(0, 1),
    });

    await expect.element(link("Dashboard")).toBeVisible();
    await expect.element(page.getByText("Viewing data for")).not.toBeInTheDocument();
    await expect.element(link("Tenants")).not.toBeInTheDocument();
  });

  it("reads the Do not disturb state the first time the alerts group opens", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(Harness, { user: MEMBER });
    flushIdle();

    await expect.element(group("Alerts")).toBeVisible();
    await Promise.resolve();
    expect(getDnd).not.toHaveBeenCalled();

    await group("Alerts").click();

    await expect
      .element(page.getByRole("switch", { name: "Toggle Do not disturb" }))
      .toBeVisible();
    await vi.waitFor(() => expect(getDnd).toHaveBeenCalledOnce());

    await group("Alerts").click();
    await group("Alerts").click();

    expect(getDnd).toHaveBeenCalledOnce();
  });

  it("never offers the switcher to a guest or a signed-out viewer", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(Harness, { user: MEMBER, isGuestSession: true, baseDomain: "example.com", tenantTargets: TENANTS });
    render(Harness, { baseDomain: "example.com" });

    await expect.element(link("Dashboard").first()).toBeVisible();
    await expect.element(page.getByText("Viewing data for")).not.toBeInTheDocument();
  });

  it("reads the Do not disturb state straight away on an alerts page", async () => {
    pageState.data = { effectivePermissions: ["*"] };
    (pageState.url as URL).pathname = "/alerts/dnd";

    render(Harness, { user: MEMBER });

    await expect
      .element(page.getByRole("switch", { name: "Toggle Do not disturb" }))
      .toBeVisible();
    await vi.waitFor(() => expect(getDnd).toHaveBeenCalledOnce());
  });

  it("holds the Do not disturb switch until the settings it saves back have been read", async () => {
    pageState.data = { effectivePermissions: ["*"] };
    let resolveRead: (settings: unknown) => void = () => {};
    getDnd.mockImplementation(() =>
      createQueryResource(
        () => new Promise((resolve) => (resolveRead = resolve)),
      ),
    );

    render(Harness, { user: MEMBER });
    flushIdle();
    await group("Alerts").click();

    const toggle = page.getByRole("switch", { name: "Toggle Do not disturb" });
    await expect.element(toggle).toBeDisabled();
    await vi.waitFor(() => expect(getDnd).toHaveBeenCalledOnce());

    (toggle.element() as HTMLElement).click();
    expect(updateDnd).not.toHaveBeenCalled();

    resolveRead({ dndManualActive: false, dndScheduleEnabled: true });

    await expect.element(toggle).toBeEnabled();
    await toggle.click();

    await vi.waitFor(() =>
      expect(updateDnd).toHaveBeenCalledWith(
        expect.objectContaining({ dndManualActive: true, dndScheduleEnabled: true }),
      ),
    );
  });

  describe("Do not disturb indicator", () => {
    const dndLink = () => page.getByRole("link", { name: "Do not disturb" });
    const showsBellOff = () =>
      document.querySelector('a[href$="/alerts/dnd"] svg.lucide-bell-off') !== null;

    async function renderWith(settings: unknown) {
      pageState.data = { effectivePermissions: ["*"] };
      (pageState.url as URL).pathname = "/alerts/dnd";
      getDnd.mockImplementation(() => remoteQuery(() => settings));
      render(Harness, { user: MEMBER });
      await vi.waitFor(() => expect(getDnd).toHaveBeenCalledOnce());
    }

    it("marks a schedule that is in effect", async () => {
      await renderWith({ dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: true });

      await expect
        .element(dndLink())
        .toHaveAttribute("title", "Scheduled Do not disturb window is on now");
      await vi.waitFor(() => expect(showsBellOff()).toBe(true));
    });

    it("says only that a schedule is configured while it is not in effect", async () => {
      await renderWith({ dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: false });

      await expect
        .element(dndLink())
        .toHaveAttribute("title", "Scheduled Do not disturb window configured");
      expect(showsBellOff()).toBe(false);
    });

    it("shows nothing when there is neither a schedule nor a mute", async () => {
      await renderWith({ dndManualActive: false, dndScheduleEnabled: false, dndScheduleActiveNow: false });

      await expect
        .element(page.getByRole("switch", { name: "Toggle Do not disturb" }))
        .toBeEnabled();
      await expect.element(dndLink()).not.toHaveAttribute("title");
      expect(showsBellOff()).toBe(false);
    });

    it("reads the settings again when the tab becomes visible", async () => {
      await renderWith({ dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: false });
      await expect
        .element(dndLink())
        .toHaveAttribute("title", "Scheduled Do not disturb window configured");

      getDnd.mockImplementation(() =>
        remoteQuery(() => ({ dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: true })),
      );
      document.dispatchEvent(new Event("visibilitychange"));

      await vi.waitFor(() => expect(getDnd).toHaveBeenCalledTimes(2));
      await expect
        .element(dndLink())
        .toHaveAttribute("title", "Scheduled Do not disturb window is on now");
    });
  });
});
