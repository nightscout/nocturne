import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WidgetId } from "#lib/api/generated/nocturne-api-client.js";
import { dashboardTopWidgets } from "#lib/stores/appearance-store.svelte.js";
import { GlucoseStatus } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { resetWatercolour } from "#lib/test-stubs/watercolour-deferral.svelte.js";

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
      entries: [{ mills: now, sgv: 123 }],
      direction: "Flat",
      demoMode: false,
      pillsData: { cob: null, basal: null, iob: null, loop: null },
      trackerInstances: [] as unknown[],
      trackerDefinitions: [] as unknown[],
      legacyFallbacks: [] as unknown[],
      timeSinceReading: Symbol("derived sentinel"),
      noteTreatmentWrite: () => {},
    },
    settings: { features: { trackerPills: { enabled: false } } },
  };
});

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));

vi.mock("#lib/api/ui-settings.remote.js", () => ({
  getUiSettings: () => remoteQuery(() => settings),
}));

const summary = vi.hoisted(() => ({ current: undefined as { mills: number; status: string } | undefined }));

vi.mock("#lib/api/generated/summaries.generated.remote.js", () => ({
  getSummary: () => remoteQuery(() => ({ current: summary.current })),
}));

vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

vi.mock("water-ui", async (importOriginal) => {
  const fake = await import("#lib/test-stubs/Watercolour.test-stub.svelte");
  return {
    ...(await importOriginal<typeof import("water-ui")>()),
    Artwork: fake.default,
    mountPlayer: fake.mountPlayer,
    bloomScene: fake.bloomScene,
  };
});

// The value indicator uses Tooltip.Root, whose provider Sidebar.Provider supplies in the
// app layout; the wrapper stands in for it.
import CurrentBGDisplay from "./current-bg-display-test-wrapper.svelte";

describe("CurrentBGDisplay", () => {
  afterEach(() => {
    store.trackerInstances = [];
    store.trackerDefinitions = [];
    settings.features.trackerPills.enabled = false;
    summary.current = undefined;
    resetWatercolour();
  });

  it("lays the wash's still over the reading on screen at once, with no bloom", async () => {
    resetWatercolour();
    dashboardTopWidgets.hydrate([WidgetId.TirChart, WidgetId.Tdd]);
    summary.current = { mills: now, status: GlucoseStatus.High };
    render(CurrentBGDisplay, { showPills: false });

    await expect.element(page.getByTestId("artwork-wash")).toBeInTheDocument();
    expect(page.getByTestId("glucose-tile-bloom").elements()).toHaveLength(0);
  });

  it("renders status text without reading a Symbol timeSinceReading value", () => {
    dashboardTopWidgets.hydrate([WidgetId.TirChart, WidgetId.Tdd]);
    expect(() => render(CurrentBGDisplay, { showPills: false })).not.toThrow();
  });

  it("keeps the reading in the header while the Current glucose widget is not showing", async () => {
    dashboardTopWidgets.hydrate([WidgetId.TirChart, WidgetId.Tdd]);
    render(CurrentBGDisplay, { showPills: false });

    await expect.element(page.getByText("123", { exact: true })).toBeInTheDocument();
  });

  it("hands the reading to the Current glucose widget when the grid shows it", async () => {
    dashboardTopWidgets.hydrate([WidgetId.TirChart, WidgetId.Tdd, WidgetId.BgDelta]);
    render(CurrentBGDisplay, { showPills: false });

    await expect.element(page.getByText("123", { exact: true })).not.toBeInTheDocument();
  });

  it("keeps the reading when the widget is stored past the grid's last slot", async () => {
    dashboardTopWidgets.hydrate([
      WidgetId.TirChart,
      WidgetId.Tdd,
      WidgetId.Clock,
      WidgetId.BgDelta,
    ]);
    render(CurrentBGDisplay, { showPills: false });

    await expect.element(page.getByText("123", { exact: true })).toBeInTheDocument();
  });

  it("hides the tracker pills while the tenant's settings turn them off", async () => {
    await page.viewport(1280, 900);
    dashboardTopWidgets.hydrate([WidgetId.TirChart, WidgetId.Tdd]);
    settings.features.trackerPills.enabled = false;
    store.trackerDefinitions = [
      { id: "def-1", name: "Sensor", category: "Sensor", dashboardVisibility: "Always", lifespanHours: 240 },
    ];
    store.trackerInstances = [
      {
        id: "inst-1",
        definitionId: "def-1",
        definitionName: "Sensor",
        category: "Sensor",
        startedAt: new Date(now - 48 * 3600_000).toISOString(),
        ageHours: 48,
        isActive: true,
      },
    ];
    render(CurrentBGDisplay, { showPills: false });

    await expect.element(page.getByText("123")).toBeVisible();
    expect(page.getByRole("button", { name: /Sensor/ }).elements()).toHaveLength(0);
  });

  it("loads the tracker completion dialog when a tracker pill is first completed", async () => {
    // The pills are hidden below the container's @md width.
    await page.viewport(1280, 900);
    settings.features.trackerPills.enabled = true;
    store.trackerDefinitions = [
      { id: "def-1", name: "Sensor", category: "Sensor", dashboardVisibility: "Always", lifespanHours: 240 },
    ];
    store.trackerInstances = [
      {
        id: "inst-1",
        definitionId: "def-1",
        definitionName: "Sensor",
        category: "Sensor",
        startedAt: new Date(now - 48 * 3600_000).toISOString(),
        ageHours: 48,
        isActive: true,
      },
    ];
    render(CurrentBGDisplay, { showPills: false });

    const dialog = page.getByTestId("tracker-completion-dialog");
    expect(dialog.elements()).toHaveLength(0);

    await page.getByRole("button", { name: /Sensor/ }).click();
    await page.getByRole("button", { name: "Complete tracker" }).click();

    await expect.element(dialog).toBeVisible();
    await expect.element(dialog.getByText("Complete Sensor")).toBeVisible();
  });
});
