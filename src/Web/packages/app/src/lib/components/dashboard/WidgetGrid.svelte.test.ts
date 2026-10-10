import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import { DEFAULT_TOP_WIDGETS, type TopWidgetId } from "./widget-registry";

const { store } = vi.hoisted(() => {
  const now = Date.UTC(2026, 5, 14, 9, 30, 0);
  return {
    store: {
      bgDelta: 4,
      lastUpdated: now,
      now,
      connectionPresentation: "pending",
      connectionError: null,
      connectionStats: { messageCount: 0 },
      timeSinceUpdate: "",
      entries: [],
      carbIntakes: [],
      trackerInstances: [],
      trackerDefinitions: [],
    },
  };
});

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));
vi.mock("$api/generated/batteries.generated.remote", () => ({
  getCurrentBatteryStatus: () => new Promise(() => {}),
}));
vi.mock("$api/generated/statistics.generated.remote", () => ({
  getMultiPeriodStatistics: () => new Promise(() => {}),
}));

import WidgetGrid from "./WidgetGrid.svelte";

const cellHeights = (container: HTMLElement) =>
  [...container.querySelectorAll<HTMLElement>("[data-slot=card] section")].map(
    (cell) => cell.getBoundingClientRect().height
  );

describe("WidgetGrid", () => {
  it.each([
    ["the default widgets", DEFAULT_TOP_WIDGETS],
    ["the reading widgets", [WidgetId.BgDelta, WidgetId.LastUpdated, WidgetId.Clock]],
    ["the status widgets", [WidgetId.ConnectionStatus, WidgetId.Meals, WidgetId.Trackers]],
    ["the daily summary", [WidgetId.DailySummary]],
  ] as [string, TopWidgetId[]][])(
    "holds each cell of %s at its loaded height while the components load, in three columns and in one",
    async (_name, widgets) => {
      for (const width of [1440, 375]) {
        await page.viewport(width, 900);
        const { container, unmount } = render(WidgetGrid, { widgets });
        expect(page.getByText("Loading").elements()).toHaveLength(widgets.length);
        const placeholderHeights = cellHeights(container);

        // The first test in a cold run also waits on the widgets' first dynamic imports,
        // which outlast waitFor's 1 s default.
        await vi.waitFor(() => expect(page.getByText("Loading").elements()).toHaveLength(0), {
          timeout: 10_000,
        });

        expect(cellHeights(container)).toEqual(placeholderHeights);
        unmount();
      }
    }
  );
});
