import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { reactiveFields } from "$lib/test-stubs/reactive-fields.svelte";

const statistics = vi.hoisted(() => ({ resolve: (_stats: unknown) => {} }));
const store = reactiveFields({
  entries: [] as { mills: number; sgv: number }[],
  currentReadingLoaded: false,
  nowMinute: Date.now(),
});

vi.mock("$lib/stores/realtime-store.svelte", async () => {
  const { resolveChartThresholds } = await import("$lib/constants/glucose-thresholds");
  return {
    getRealtimeStore: () => store,
    tryGetRealtimeStore: () => store,
    tenantChartThresholds: () => ({
      get current() {
        return resolveChartThresholds();
      },
    }),
  };
});
vi.mock("$api/generated/statistics.generated.remote", () => ({
  getMultiPeriodStatistics: () =>
    new Promise((resolve) => {
      statistics.resolve = resolve;
    }),
}));

import TirChartWidget from "./TirChartWidget.svelte";
import TddWidget from "./TddWidget.svelte";
import DailySummaryWidget from "./DailySummaryWidget.svelte";

const stats = {
  lastDay: {
    entryCount: 288,
    analytics: {
      timeInRange: {
        percentages: { veryLow: 1, low: 3, target: 70, high: 20, veryHigh: 6 },
      },
    },
    insulinDelivery: { manualBolusPerDay: 18, automaticBolusPerDay: 0, totalBasalPerDay: 22, tdd: 40 },
    treatmentSummary: { dailyCarbs: 120 },
  },
  last90Days: {
    periodDays: 90,
    entryCount: 25000,
    analytics: {
      timeInRange: {
        percentages: { veryLow: 1, low: 2, target: 68, high: 22, veryHigh: 7 },
      },
    },
    insulinDelivery: { manualBolusPerDay: 16, automaticBolusPerDay: 0, totalBasalPerDay: 24, tdd: 40 },
    treatmentSummary: { dailyCarbs: 111 },
  },
};

const withMicroboluses = structuredClone(stats);
withMicroboluses.lastDay.insulinDelivery.automaticBolusPerDay = 6;
withMicroboluses.last90Days.insulinDelivery.automaticBolusPerDay = 5;

const cellHeight = (container: HTMLElement) =>
  container.querySelector("section")!.getBoundingClientRect().height;

describe("stat widgets", () => {
  it.each([
    ["Time in Range", TirChartWidget, "in range", stats],
    ["Total Daily Dose", TddWidget, "Basal", stats],
    ["Total Daily Dose with microboluses", TddWidget, "Auto", withMicroboluses],
  ])(
    "%s keeps its height when its statistics arrive, in three columns and in one",
    async (_name, Widget, loadedText, data) => {
      for (const width of [1440, 375]) {
        await page.viewport(width, 900);
        const { container, unmount } = render(Widget);
        const skeletonHeight = cellHeight(container);

        statistics.resolve(data);
        await expect.element(page.getByText(loadedText, { exact: true })).toBeVisible();

        expect(cellHeight(container)).toBe(skeletonHeight);
        unmount();
      }
    }
  );

  it("Total Daily Dose shows an Auto row only for accounts with automatic boluses", async () => {
    await page.viewport(1440, 900);
    const { unmount } = render(TddWidget);
    statistics.resolve(stats);
    await expect.element(page.getByText("Basal", { exact: true })).toBeVisible();
    expect(page.getByText("Auto", { exact: true }).elements().filter((el) => el.checkVisibility({ visibilityProperty: true }))).toHaveLength(0);
    unmount();

    render(TddWidget);
    statistics.resolve(withMicroboluses);
    await expect.element(page.getByText("Auto", { exact: true })).toBeVisible();
  });

  it("Total Daily Dose labels its 24-hour view Last 24h", async () => {
    await page.viewport(1440, 900);
    render(TddWidget);
    statistics.resolve(stats);

    await expect.element(page.getByRole("button", { name: "Last 24h" })).toBeVisible();
    await expect.element(page.getByText("Today", { exact: true })).not.toBeInTheDocument();
  });

  it("Total Daily Dose shows the backend's per-day figures as given, without dividing them", async () => {
    await page.viewport(1440, 900);
    render(TddWidget);
    statistics.resolve(stats);

    await expect.element(page.getByText("18.0 U", { exact: true })).toBeVisible();
    await expect.element(page.getByText("22.0 U", { exact: true })).toBeVisible();
    await expect.element(page.getByText("120 g", { exact: true })).toBeVisible();

    await page.getByRole("button", { name: "Last 24h" }).click();

    await expect.element(page.getByText("16.0 U", { exact: true })).toBeVisible();
    await expect.element(page.getByText("24.0 U", { exact: true })).toBeVisible();
    await expect.element(page.getByText("111 g", { exact: true })).toBeVisible();
    await expect.element(page.getByText("40.0", { exact: true })).toBeVisible();
  });

  it("Today's Summary keeps its height when the day's readings arrive, in three columns and in one", async () => {
    for (const width of [1440, 375]) {
      await page.viewport(width, 900);
      store.entries = [];
      store.currentReadingLoaded = false;
      const { container, unmount } = render(DailySummaryWidget);
      const skeletonHeight = cellHeight(container);

      // Midday on the store's own clock: readings minutes before a wall-clock "now" fall on
      // yesterday when the run is in the first minutes after midnight.
      const now = new Date(2026, 9, 21, 12, 0).getTime();
      store.nowMinute = now;
      store.entries = [
        { mills: now - 600_000, sgv: 110 },
        { mills: now - 300_000, sgv: 150 },
      ];
      store.currentReadingLoaded = true;
      await expect.element(page.getByText("readings")).toBeVisible();

      expect(cellHeight(container)).toBe(skeletonHeight);
      unmount();
    }
  });
});
