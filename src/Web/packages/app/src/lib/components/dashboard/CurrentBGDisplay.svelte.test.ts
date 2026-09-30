import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import { dashboardTopWidgets } from "$lib/stores/appearance-store.svelte";

const now = Date.UTC(2026, 5, 14, 9, 30, 0);

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => ({
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
    trackerInstances: [],
    trackerDefinitions: [],
    timeSinceReading: Symbol("derived sentinel"),
  }),
}));

vi.mock("$lib/stores/settings-store.svelte", () => ({
  getSettingsStore: () => ({
    features: { trackerPills: { enabled: false } },
  }),
}));

// The value indicator uses Tooltip.Root, whose provider Sidebar.Provider supplies in the
// app layout; the wrapper stands in for it.
import CurrentBGDisplay from "./current-bg-display-test-wrapper.svelte";

describe("CurrentBGDisplay", () => {
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
});
