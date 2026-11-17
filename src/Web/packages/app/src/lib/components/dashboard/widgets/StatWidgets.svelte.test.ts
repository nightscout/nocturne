import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { GlycemicVariabilityStatus, TimeZoneUnavailableReason } from "#lib/api/index.js";
import { reactiveFields } from "#lib/test-stubs/reactive-fields.svelte.js";

const statistics = vi.hoisted(() => ({ resolve: (_stats: unknown) => {} }));
const store = reactiveFields({});

vi.mock("#lib/stores/realtime-store.svelte.js", async () => {
  const { resolveChartThresholds } = await import("#lib/constants/glucose-thresholds.js");
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
vi.mock("#lib/api/generated/statistics.generated.remote.js", () => ({
  getMultiPeriodStatistics: () =>
    new Promise((resolve) => {
      statistics.resolve = resolve;
    }),
}));

import WithTooltipProvider from "#lib/test-stubs/WithTooltipProvider.test-stub.svelte";
import WidgetContainerHarness from "./WidgetContainerHarness.test.svelte";
import TirChartWidget from "./TirChartWidget.svelte";
import TddWidget from "./TddWidget.svelte";
import DailySummaryWidget from "./DailySummaryWidget.svelte";

const stats = {
  lastDay: {
    entryCount: 288,
    analytics: {
      basicStats: { count: 288, mean: 144, min: 60, max: 260 },
      glycemicVariability: { coefficientOfVariation: 40, coefficientOfVariationStatus: GlycemicVariabilityStatus.Moderate },
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

const unreliable = { meetsReliabilityCriteria: false, daysOfData: 1, recommendedMinimumDays: 14 };
const withoutZone = {
  lastDay: {
    ...stats.lastDay,
    reliability: unreliable,
    analytics: { ...stats.lastDay.analytics, timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured },
  },
  last90Days: { ...stats.last90Days, reliability: unreliable },
};

const cellHeight = (container: HTMLElement) =>
  container.querySelector("section")!.getBoundingClientRect().height;

describe("stat widgets", () => {
  it.each([
    ["Time in range", TirChartWidget, "in range", stats],
    ["Total daily dose", TddWidget, "Basal", stats],
    ["Total Daily Dose with microboluses", TddWidget, "Auto", withMicroboluses],
  ])(
    "%s keeps its height when its statistics arrive, in three columns and in one",
    async (_name, Widget, loadedText, data) => {
      for (const [width, wide] of [
        [1440, true],
        [375, false],
      ] as const) {
        await page.viewport(width, 900);
        const { container, unmount } = render(WidgetContainerHarness, { props: { component: Widget, wide } });
        const skeletonHeight = cellHeight(container);

        statistics.resolve(data);
        await expect.element(page.getByText(loadedText, { exact: true })).toBeVisible();

        expect(cellHeight(container)).toBe(skeletonHeight);
        unmount();
      }
    }
  );

  it("Time in range draws a bar beside the other widgets and a column when they stack", async () => {
    for (const [width, wide] of [
      [1440, true],
      [375, false],
    ] as const) {
      await page.viewport(width, 900);
      const { container, unmount } = render(WidgetContainerHarness, {
        props: { component: TirChartWidget, wide },
      });
      statistics.resolve(stats);
      await expect.element(page.getByText("in range", { exact: true })).toBeVisible();

      const heights = [...container.querySelectorAll("svg")]
        .filter((svg) => svg.checkVisibility())
        .map((svg) => svg.getBoundingClientRect().height);
      expect(heights.length).toBeGreaterThan(0);
      const tallest = Math.max(...heights);
      if (wide) expect(tallest).toBeLessThanOrEqual(24);
      else expect(tallest).toBeGreaterThan(100);
      expect(page.getByText("1% Very low").elements().some((el) => el.checkVisibility())).toBe(
        !wide
      );
      unmount();
    }
  });

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

  it.each([
    ["Time in range", TirChartWidget],
    ["Total daily dose", TddWidget],
  ])("%s says days are UTC days only for the period whose data has no time zone", async (_name, Widget) => {
    await page.viewport(1440, 900);
    render(WithTooltipProvider, { props: { component: Widget } });
    statistics.resolve(withoutZone);

    await expect.element(page.getByRole("button", { name: "Counted on UTC days" })).toBeVisible();
    await expect.element(page.getByText("Days of data are counted on UTC days.")).not.toBeInTheDocument();

    await page.getByRole("button", { name: "Last 24h" }).click();

    await expect.element(page.getByText("90-Day Avg", { exact: true })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Counted on UTC days" })).not.toBeInTheDocument();
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

  it("Last 24h summary keeps its height when its statistics arrive, in three columns and in one", async () => {
    for (const width of [1440, 375]) {
      await page.viewport(width, 900);
      const { container, unmount } = render(DailySummaryWidget);
      const skeletonHeight = cellHeight(container);

      statistics.resolve(stats);
      await expect.element(page.getByText("readings")).toBeVisible();

      expect(cellHeight(container)).toBe(skeletonHeight);
      unmount();
    }
  });

  it("Last 24h summary shows the backend's figures as given, with no figure of its own", async () => {
    await page.viewport(1440, 900);
    render(DailySummaryWidget);
    statistics.resolve(stats);

    await expect.element(page.getByText("144", { exact: true })).toBeVisible();
    await expect.element(page.getByText("↓ 60", { exact: true })).toBeVisible();
    await expect.element(page.getByText("↑ 260", { exact: true })).toBeVisible();
    await expect.element(page.getByText("70%", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Moderate (40% CV)")).toBeVisible();
    await expect.element(page.getByText("288 readings")).toBeVisible();
  });

  it.each([
    [GlycemicVariabilityStatus.Stable, 30, "Stable (30% CV)"],
    [GlycemicVariabilityStatus.Moderate, 40, "Moderate (40% CV)"],
    [GlycemicVariabilityStatus.Variable, 55, "Variable (55% CV)"],
  ])("Last 24h summary renders the served %s variability status", async (status, cv, text) => {
    await page.viewport(1440, 900);
    render(DailySummaryWidget);
    statistics.resolve({
      lastDay: {
        ...stats.lastDay,
        analytics: {
          ...stats.lastDay.analytics,
          glycemicVariability: { coefficientOfVariation: cv, coefficientOfVariationStatus: status },
        },
      },
    });

    await expect.element(page.getByText(text)).toBeVisible();
  });

  it("Last 24h summary shows the served status even when it disagrees with the figure", async () => {
    await page.viewport(1440, 900);
    render(DailySummaryWidget);
    statistics.resolve({
      lastDay: {
        ...stats.lastDay,
        analytics: {
          ...stats.lastDay.analytics,
          glycemicVariability: { coefficientOfVariation: 10, coefficientOfVariationStatus: GlycemicVariabilityStatus.Variable },
        },
      },
    });

    await expect.element(page.getByText("Variable (10% CV)")).toBeVisible();
  });

  it("Last 24h summary says so when the last 24 hours hold no readings", async () => {
    await page.viewport(1440, 900);
    render(DailySummaryWidget);
    statistics.resolve({ lastDay: { entryCount: 0, analytics: { basicStats: { count: 0 } } } });

    await expect.element(page.getByText("No data in the last 24h")).toBeVisible();
  });
});
