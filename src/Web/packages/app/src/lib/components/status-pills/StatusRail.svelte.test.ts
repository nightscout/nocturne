import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import { LegacyConsumableKind, RailItemId } from "$lib/api/generated/nocturne-api-client";

const { now, store, settings } = vi.hoisted(() => {
  const now = Date.UTC(2026, 5, 14, 9, 30, 0);
  return {
    now,
    store: {
      currentBG: 123,
      currentEntry: { mills: now, sgv: 123 },
      bgDelta: 4,
      lastUpdated: now,
      now,
      isConnected: true,
      isReady: true,
      entries: [{ mills: now, sgv: 123 }],
      direction: "Flat",
      demoMode: false,
      pillsData: { cob: null, basal: null, iob: null, loop: null },
      currentReservoir: 120 as number | null,
      trackerInstances: [] as unknown[],
      trackerDefinitions: [] as unknown[],
      legacyFallbacks: [] as unknown[],
      refreshTrackers: () => Promise.resolve(),
    },
    settings: { features: { trackerPills: { enabled: true } } },
  };
});

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));

vi.mock("$api/ui-settings.remote", () => ({
  getUiSettings: () => remoteQuery(() => settings),
}));

import { dashboardRail, hiddenLegacyFallbacks } from "$lib/stores/appearance-store.svelte";
import CurrentBGDisplay from "$lib/components/dashboard/current-bg-display-test-wrapper.svelte";

const SENSOR_DEFINITION = {
  id: "def-1",
  name: "Sensor",
  category: "Sensor",
  dashboardVisibility: "Always",
  lifespanHours: 240,
};
const SENSOR_INSTANCE = {
  id: "inst-1",
  definitionId: "def-1",
  definitionName: "Sensor",
  category: "Sensor",
  startedAt: new Date(now - 48 * 3600_000).toISOString(),
  ageHours: 48,
  isActive: true,
};

const SITE_FALLBACK = {
  kind: "Site",
  latestEventAt: new Date(now - 50 * 3600_000).toISOString(),
  ageHours: 50,
  schedule: [],
  spec: { name: "Infusion Site", category: "Cannula", lifespanHours: 72 },
};

const DESKTOP = "status-pills";
const MOBILE = "status-pills-mobile";
// The pref writes through to localStorage, which every later test file's store reads at load.
const RAIL_STORAGE_KEY = "nocturne-dashboard-rail";
const HIDDEN_STORAGE_KEY = "nocturne-hidden-legacy-fallbacks";

const { Cob, Basal, Iob, Loop, Reservoir, Trackers } = RailItemId;

/** The rail items a rail renders, in the order it renders them. */
function railItems(testId: string): string[] {
  const rail = page.getByTestId(testId).element();
  return [...rail.querySelectorAll("[data-rail-item]")].map(
    (item) => item.getAttribute("data-rail-item") ?? ""
  );
}

describe("StatusRail", () => {
  beforeEach(async () => {
    await page.viewport(1280, 900);
    dashboardRail.presentFrom({});
    settings.features.trackerPills.enabled = true;
    store.currentReservoir = 120;
    store.trackerDefinitions = [SENSOR_DEFINITION];
    store.trackerInstances = [SENSOR_INSTANCE];
    store.legacyFallbacks = [];
    hiddenLegacyFallbacks.presentFrom({});
  });

  afterEach(() => {
    dashboardRail.presentFrom({});
    localStorage.removeItem(RAIL_STORAGE_KEY);
    store.trackerInstances = [];
    store.trackerDefinitions = [];
    store.legacyFallbacks = [];
    hiddenLegacyFallbacks.presentFrom({});
    localStorage.removeItem(HIDDEN_STORAGE_KEY);
  });

  /** The tracker pill buttons inside a rail, in the order they render. */
  function trackerPills(testId: string): string[] {
    const trackers = page.getByTestId(testId).element().querySelector(`[data-rail-item="${Trackers}"]`);
    return [...(trackers?.querySelectorAll("button") ?? [])].map(
      (button) => button.getAttribute("data-testid") ?? "tracker-pill"
    );
  }

  it("shows a legacy age after the tracker pills, on desktop and mobile", async () => {
    store.legacyFallbacks = [SITE_FALLBACK];
    render(CurrentBGDisplay);

    await expect
      .element(page.getByTestId(DESKTOP).getByRole("button", { name: /Infusion site/ }))
      .toBeVisible();
    expect(trackerPills(DESKTOP)).toEqual(["tracker-pill", "legacy-age-pill"]);

    await page.viewport(400, 900);
    await expect
      .element(page.getByTestId(MOBILE).getByRole("button", { name: /Infusion site/ }))
      .toBeVisible();
  });

  it("shows the trackers item for a legacy age alone", async () => {
    store.trackerInstances = [];
    store.legacyFallbacks = [SITE_FALLBACK];
    render(CurrentBGDisplay);

    await expect
      .element(page.getByTestId(DESKTOP).getByRole("button", { name: /Infusion site/ }))
      .toBeVisible();
    expect(railItems(DESKTOP)).toContain(Trackers);
  });

  it("does not show a legacy age the user hid", async () => {
    store.legacyFallbacks = [SITE_FALLBACK];
    hiddenLegacyFallbacks.hydrate([LegacyConsumableKind.Site]);
    render(CurrentBGDisplay);

    await expect
      .element(page.getByTestId(DESKTOP).getByRole("button", { name: /Sensor/ }))
      .toBeVisible();
    expect(page.getByRole("button", { name: /Infusion site/ }).elements()).toHaveLength(0);
  });

  it("shows the ordered items first and the rest after them in default order", async () => {
    dashboardRail.hydrate({ order: [Reservoir, Iob] });
    render(CurrentBGDisplay);

    await expect.element(page.getByTestId(DESKTOP).getByText("Reservoir")).toBeVisible();
    expect(railItems(DESKTOP)).toEqual([Reservoir, Iob, Cob, Basal, Loop, Trackers]);
  });

  it("does not show an item the user hid", async () => {
    dashboardRail.hydrate({ hidden: [Basal, Loop, Trackers] });
    render(CurrentBGDisplay);

    await expect.element(page.getByTestId(DESKTOP).getByText("COB")).toBeVisible();
    expect(railItems(DESKTOP)).toEqual([Cob, Iob, Reservoir]);
    expect(page.getByRole("button", { name: /Sensor/ }).elements()).toHaveLength(0);
  });

  it("shows every item, trackers included, for the default preference", async () => {
    render(CurrentBGDisplay);

    await expect
      .element(page.getByTestId(DESKTOP).getByRole("button", { name: /Sensor/ }))
      .toBeVisible();
    expect(railItems(DESKTOP)).toEqual([Cob, Basal, Iob, Loop, Reservoir, Trackers]);
  });

  it("hides trackers while the tenant turns tracker pills off, even where the user placed them", async () => {
    settings.features.trackerPills.enabled = false;
    dashboardRail.hydrate({ order: [Trackers, Cob] });
    render(CurrentBGDisplay);

    await expect.element(page.getByTestId(DESKTOP).getByText("COB")).toBeVisible();
    expect(railItems(DESKTOP)).toEqual([Cob, Basal, Iob, Loop, Reservoir]);
    expect(page.getByRole("button", { name: /Sensor/ }).elements()).toHaveLength(0);
  });

  it("keeps the reservoir out while the pump reports none", async () => {
    store.currentReservoir = null;
    render(CurrentBGDisplay);

    await expect.element(page.getByTestId(DESKTOP).getByText("COB")).toBeVisible();
    expect(railItems(DESKTOP)).not.toContain(Reservoir);
  });

  it("shows the same rail, trackers included, in the mobile layout", async () => {
    await page.viewport(400, 900);
    dashboardRail.hydrate({ order: [Trackers, Loop, Cob], hidden: [Basal, Iob, Reservoir] });
    render(CurrentBGDisplay);

    const mobile = page.getByTestId(MOBILE);
    await expect.element(mobile.getByRole("button", { name: /Sensor/ })).toBeVisible();
    await expect.element(page.getByTestId(DESKTOP)).not.toBeVisible();
    expect(railItems(MOBILE)).toEqual([Trackers, Loop, Cob]);
  });

  it("leaves out the mobile rail while none of its items has anything to show", async () => {
    await page.viewport(400, 900);
    store.trackerInstances = [];
    store.currentReservoir = null;
    dashboardRail.hydrate({ hidden: [Cob, Basal, Iob, Loop] });
    render(CurrentBGDisplay);

    await expect.element(page.getByText("123")).toBeInTheDocument();
    expect(page.getByTestId(MOBILE).elements()).toHaveLength(0);
  });

  it("hides the mobile layout at desktop width", async () => {
    render(CurrentBGDisplay);

    await expect.element(page.getByTestId(DESKTOP)).toBeVisible();
    await expect.element(page.getByTestId(MOBILE)).not.toBeVisible();
  });
});
