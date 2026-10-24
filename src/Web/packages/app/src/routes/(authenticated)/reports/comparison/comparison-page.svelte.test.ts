import { render } from "vitest-browser-svelte";
import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type {
  ExtendedGlucoseAnalytics,
  InsulinDeliveryStatistics,
  PersonalRangeTimeInRange,
} from "$api/generated/nocturne-api-client";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

type Extras = {
  insulinDelivery?: InsulinDeliveryStatistics;
  personalRange?: PersonalRangeTimeInRange;
};

const emptyInsulin: InsulinDeliveryStatistics = { insulinEventCount: 0, carbCount: 0 };

let periodA: ExtendedGlucoseAnalytics;
let periodB: ExtendedGlucoseAnalytics;
let extrasA: Extras;
let extrasB: Extras;

vi.mock("$api/reports.remote", () => ({
  getReportsAnalysis: (input: { from: string }) =>
    remoteQuery(() =>
      input.from === "2026-09-01"
        ? { analysis: periodA, personalRange: extrasA.personalRange }
        : { analysis: periodB, personalRange: extrasB.personalRange }
    ),
  getReportsInsulinDelivery: (input: { from: string }) =>
    remoteQuery(
      () => (input.from === "2026-09-01" ? extrasA.insulinDelivery : extrasB.insulinDelivery) ?? emptyInsulin
    ),
}));

vi.mock("runed/kit", () => ({
  useSearchParams: () => ({
    preset: "custom",
    aFrom: "2026-09-01",
    aTo: "2026-09-01",
    bFrom: "2026-09-02",
    bTo: "2026-09-02",
    aLabel: "A",
    bLabel: "B",
    update: vi.fn(),
  }),
}));

vi.mock("$lib/components/reports/TIRStackedChart.svelte", () =>
  import("$lib/test-stubs/Empty.test-stub.svelte")
);

import ComparisonPage from "./+page.svelte";

function analysis(lowMinutesPerDay: number, lowEventsPerDay: number): ExtendedGlucoseAnalytics {
  return {
    basicStats: { count: 20 },
    timeInRange: {
      averageDailyMinutes: { low: lowMinutesPerDay, high: 60 },
      averageDailyEpisodes: { belowRange: lowEventsPerDay, aboveRange: 2 },
    },
  };
}

async function cells(label: string) {
  const locator = page.getByText(label, { exact: true });
  await expect.element(locator).toBeVisible();
  const row = locator.element().parentElement!;
  return Array.from(row.children).map((cell) => cell.textContent?.trim());
}

describe("hypo comparison rows", () => {
  beforeEach(() => {
    periodA = analysis(0, 0);
    periodB = analysis(30, 1.5);
    extrasA = {};
    extrasB = {};
  });

  it("shows per-day time and events and a positive change from a measured zero", async () => {
    render(ComparisonPage);
    const duration = await cells("Hypo time / day");
    expect(duration.slice(1, 3)).toEqual(["0m", "30m"]);
    expect(duration[4]).toBe("+30m");
    const events = await cells("Hypo events / day");
    expect(events.slice(1, 3)).toEqual(["0.00", "1.50"]);
    expect(events[4]).toBe("+1.50");
    expect((await cells("Hyper time / day"))[4]).toBe("±0m");
    expect((await cells("Hyper events / day"))[4]).toBe("±0");
  });

  it("shows a low rate that one decimal would round to zero", async () => {
    periodA = analysis(0, 0.03);
    periodB = analysis(0, 0.04);
    render(ComparisonPage);
    const events = await cells("Hypo events / day");
    expect(events.slice(1, 3)).toEqual(["0.03", "0.04"]);
    expect(events[4]).toBe("+0.01");
  });

  it("shows a negative change when the second period has fewer lows", async () => {
    periodA = analysis(90, 3);
    render(ComparisonPage);
    expect((await cells("Hypo time / day"))[4]).toBe("−1h");
    expect((await cells("Hypo events / day"))[4]).toBe("−1.50");
  });

  it("shows no data instead of comparing a missing period against zero", async () => {
    periodA = { basicStats: { count: 0 } };
    periodB = analysis(0, 0);
    render(ComparisonPage);
    const duration = await cells("Hypo time / day");
    expect(duration.slice(1, 3)).toEqual(["No data", "0m"]);
    expect(duration[4]).toBe("—");
    const events = await cells("Hypo events / day");
    expect(events.slice(1, 3)).toEqual(["No data", "0.00"]);
    expect(events[4]).toBe("—");
  });
});

describe("grouped comparison rows", () => {
  const rich = (scale: number): ExtendedGlucoseAnalytics => ({
    basicStats: { count: 20, mean: 140 * scale, median: 130 * scale },
    timeInRange: {
      percentages: { target: 70, veryLow: 1 * scale, low: 3 * scale, high: 20 * scale, veryHigh: 6 * scale },
      averageDailyMinutes: { low: 0, high: 60 },
      averageDailyEpisodes: { belowRange: 0, aboveRange: 2 },
    },
    glycemicVariability: {
      standardDeviation: 40 * scale,
      lowBloodGlucoseIndex: 1.2 * scale,
      highBloodGlucoseIndex: 4 * scale,
      meanAmplitudeGlycemicExcursions: 90 * scale,
      continuousOverlappingNetGlycemicAction: 100 * scale,
      averageDailyRiskRange: 20 * scale,
    },
    dataQuality: { cgmActivePercent: 90 * scale },
    dataSufficiency: { daysWithData: 7 * scale, averageReadingsPerDay: 280 * scale },
  });

  const delivery = (tdd: number, scale = 1): InsulinDeliveryStatistics => ({
    insulinEventCount: 12,
    carbCount: 3,
    tdd,
    totalBasalPerDay: 20.5 * scale,
    basalPercent: 45 * scale,
    bolusesPerDay: 4.5 * scale,
    carbsPerDay: 150 * scale,
    icRatio: 11.5 * scale,
  });

  beforeEach(() => {
    periodA = rich(1);
    periodB = rich(1.1);
    extrasA = { insulinDelivery: delivery(44.2) };
    extrasB = { insulinDelivery: delivery(46.9, 1.2) };
  });

  it("renders each group heading in order, the overview first", async () => {
    render(ComparisonPage);
    await expect.element(page.getByText("Overview", { exact: true })).toBeVisible();
    const headings = [
      "Overview",
      "Range detail",
      "Variability and risk",
      "Insulin and carbs",
      "Data quality",
    ];
    for (const h of headings) {
      await expect.element(page.getByRole("heading", { name: h, level: 3 })).toBeVisible();
    }
    const tops = headings.map((h) => page.getByText(h, { exact: true }).element().getBoundingClientRect().top);
    expect(tops).toEqual([...tops].sort((x, y) => x - y));
  });

  it("shows every row of the new groups for both periods with a change", async () => {
    render(ComparisonPage);
    for (const label of [
      "Time Very Low (%)",
      "Time Low (%)",
      "Time High (%)",
      "Time Very High (%)",
      "Standard deviation",
      "Median glucose",
      "LBGI",
      "HBGI",
      "MAGE",
      "CONGA",
      "ADRR",
      "Total insulin / day",
      "Basal / day",
      "Basal share of insulin",
      "Manual boluses / day",
      "Carbs / day",
      "Carb-to-insulin ratio",
      "CGM active time",
      "Days with data",
      "Readings / day",
    ]) {
      const row = await cells(label);
      expect(row[1], `${label} period A`).not.toBe("No data");
      expect(row[2], `${label} period B`).not.toBe("No data");
      expect(row[4], `${label} change`).toMatch(/^\+/);
    }
  });

  it("shows the API's per-day insulin unchanged", async () => {
    render(ComparisonPage);
    const insulin = await cells("Total insulin / day");
    expect(insulin.slice(1, 3)).toEqual(["44.2 U", "46.9 U"]);
    expect(insulin[4]).toBe("+2.7 U");
    expect((await cells("Basal / day")).slice(1, 3)).toEqual(["20.5 U", "24.6 U"]);
    expect((await cells("Carbs / day")).slice(1, 3)).toEqual(["150 g", "180 g"]);
    expect((await cells("Carb-to-insulin ratio")).slice(1, 3)).toEqual(["11.5 g/U", "13.8 g/U"]);
  });

  it("shows no data for insulin figures of a period with no insulin records", async () => {
    extrasA = { insulinDelivery: { insulinEventCount: 0, carbCount: 0, tdd: 0, carbsPerDay: 0 } };
    render(ComparisonPage);
    const insulin = await cells("Total insulin / day");
    expect(insulin.slice(1, 3)).toEqual(["No data", "46.9 U"]);
    expect(insulin[4]).toBe("—");
    expect((await cells("Carbs / day"))[1]).toBe("No data");
  });

  it("shows glucose-valued variability rows in the chosen unit", async () => {
    glucoseUnits.current = "mmol";
    try {
      render(ComparisonPage);
      const sd = await cells("Standard deviation");
      expect(sd.slice(1, 3)).toEqual(["2.2 mmol/L", "2.4 mmol/L"]);
      expect(sd[4]).toBe("+0.2 mmol/L");
    } finally {
      glucoseUnits.current = "mg/dl";
    }
  });

  it("shows the personal range row only when the API returns a personal range", async () => {
    render(ComparisonPage);
    await cells("Time Very Low (%)");
    expect(page.getByText("Time in personal range", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows time in the personal range for both periods when returned", async () => {
    extrasA = { ...extrasA, personalRange: { inRangePercent: 55 } };
    extrasB = { ...extrasB, personalRange: { inRangePercent: 61.5 } };
    render(ComparisonPage);
    const row = await cells("Time in personal range");
    expect(row.slice(1, 3)).toEqual(["55.0%", "61.5%"]);
    expect(row[4]).toBe("+6.5 pp");
  });
});
