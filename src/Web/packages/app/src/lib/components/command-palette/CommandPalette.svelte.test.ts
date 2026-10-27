import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";

const { goto, store, NOW } = vi.hoisted(() => {
  const NOW = Date.UTC(2026, 9, 25, 12, 0, 0);
  return {
    NOW,
    goto: vi.fn(() => Promise.resolve()),
    store: {
      pillsData: {},
      now: NOW,
      legacyFallbacks: [] as unknown[],
      legacyCoverage: [] as unknown[],
      trackerInstances: [] as unknown[],
    },
  };
});

vi.mock("$app/navigation", () => ({ goto }));

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
}));

// The vitals strip reads live glucose; the palette under test only owns its items.
vi.mock("./CommandPaletteVitals.svelte", () => ({ default: () => {} }));

import CommandPalette from "./CommandPalette.svelte";
import { pinnedItemIds, recentItemIds } from "./command-palette-store.svelte";

const item = (name: string) => page.getByRole("option", { name, exact: true });

// A report option's name carries its description after the title.
const report = (title: string) =>
  page.getByRole("option", { name: new RegExp(`^${title}`) });

describe("CommandPalette", () => {
  beforeEach(() => {
    goto.mockClear();
    pinnedItemIds.current = [];
    recentItemIds.current = [];
    pageState.data = { effectivePermissions: [], isPlatformAdmin: false };
    store.legacyFallbacks = [];
    store.legacyCoverage = [];
    store.trackerInstances = [];
  });

  // The palette's ages run on the store's clock from the timestamps the server sent.
  const hoursAgo = (hours: number) => new Date(NOW - hours * 3600_000).toISOString();

  it("shows an uncovered kind's age from its legacy fallback", async () => {
    store.legacyFallbacks = [{ kind: "Site", latestEventAt: hoursAgo(50), ageHours: 1 }];

    render(CommandPalette, { open: true });

    await expect.element(page.getByRole("option", { name: /^CAGE: 2d 2h/ })).toBeVisible();
  });

  it("shows a covered kind's age from the newest run of a covering tracker", async () => {
    store.legacyCoverage = [{ kind: "Sensor", definitionIds: ["dexcom", "libre"] }];
    store.trackerInstances = [
      { id: "a", definitionId: "dexcom", startedAt: hoursAgo(30) },
      { id: "b", definitionId: "libre", startedAt: hoursAgo(5) },
      { id: "c", definitionId: "appointment", startedAt: hoursAgo(1) },
    ];

    render(CommandPalette, { open: true });

    await expect.element(page.getByRole("option", { name: /^SAGE: 5h/ })).toBeVisible();
  });

  it("shows no age for a covered kind whose trackers are not running", async () => {
    store.legacyCoverage = [{ kind: "Sensor", definitionIds: ["dexcom"] }];
    store.trackerInstances = [{ id: "c", definitionId: "appointment", startedAt: hoursAgo(1) }];

    render(CommandPalette, { open: true });

    await expect.element(page.getByRole("option", { name: /^SAGE(?!:)/ })).toBeVisible();
  });

  it("renders no button inside an option or link", async () => {
    render(CommandPalette, { open: true, tenantless: true });

    // The palette selects its first item a tick after mounting; unmounting before then
    // leaves bits-ui reading a detached ref.
    await expect
      .element(page.getByRole("option").first())
      .toHaveAttribute("aria-selected", "true");

    expect(
      page.getByRole("option").getByRole("button").elements()
    ).toHaveLength(0);
    expect(page.getByRole("link").getByRole("button").elements()).toHaveLength(
      0
    );
  });

  it("pins an item without following its link", async () => {
    // Captured before the pin's own handler runs, so stopping propagation cannot hide it.
    let clickedInsideLink = false;
    const recordClick = (event: MouseEvent) => {
      const target = event.target;
      if (target instanceof Element && target.closest("a"))
        clickedInsideLink = true;
      event.preventDefault();
    };
    window.addEventListener("click", recordClick, true);
    render(CommandPalette, { open: true, tenantless: true });

    await page.getByRole("button", { name: "Pin Appearance" }).click();
    window.removeEventListener("click", recordClick, true);

    await expect
      .element(page.getByRole("button", { name: "Unpin Appearance" }))
      .toBeInTheDocument();
    expect(pinnedItemIds.current).toContain("settings-appearance");
    expect(goto).not.toHaveBeenCalled();
    expect(clickedInsideLink).toBe(false);
  });

  it("offers Audit to a viewer holding audit.read", async () => {
    pageState.data = { effectivePermissions: ["audit.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Audit")).toBeVisible();
  });

  it("offers Audit to a viewer holding audit.manage", async () => {
    pageState.data = { effectivePermissions: ["audit.manage"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Audit")).toBeVisible();
  });

  it("withholds Audit from a viewer holding neither audit scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Audit")).not.toBeInTheDocument();
  });

  it("offers Alerts to a viewer holding alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Alerts")).toBeVisible();
  });

  it("withholds Alerts from a viewer holding neither alerts scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Alerts")).not.toBeInTheDocument();
  });

  it("offers Grants to a viewer holding sharing.manage", async () => {
    pageState.data = { effectivePermissions: ["sharing.manage"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Grants")).toBeVisible();
  });

  it("withholds Grants from a viewer without sharing.manage", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Grants")).not.toBeInTheDocument();
  });

  it("offers the platform admin pages to a platform administrator", async () => {
    pageState.data = { effectivePermissions: [], isPlatformAdmin: true };
    render(CommandPalette, { open: true });

    await expect.element(item("All tenants")).toBeVisible();
    await expect.element(item("Access requests")).toBeVisible();
  });

  it("withholds the platform admin pages from everyone else", async () => {
    pageState.data = { effectivePermissions: ["*"], isPlatformAdmin: false };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("All tenants")).not.toBeInTheDocument();
    await expect.element(item("Access requests")).not.toBeInTheDocument();
  });

  it("lists a report under the registry's title to a viewer holding its scopes", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(report("Day-by-day view")).toBeVisible();
  });

  it("withholds a report from a viewer without its scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(report("Day-by-day view")).toBeVisible();
    await expect.element(report("Battery")).not.toBeInTheDocument();
  });
});
