import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import {
  HourlyComparison,
  type HourlyPattern,
  type HourlyPatterns,
} from "$lib/api/generated/nocturne-api-client";
import HourlyGlance from "./HourlyGlance.svelte";

const hour = (h: number, inRange: number): HourlyPattern => ({
  hour: h,
  inRange,
  belowRange: 0,
  aboveRange: 100 - inRange,
  isRanked: true,
  timeInRange: { tightTarget: inRange },
});

const report = (overrides: Partial<HourlyPatterns>): HourlyPatterns => ({
  comparison: HourlyComparison.Ranked,
  bestHours: [],
  worstHours: [],
  mostBelowRangeHours: [],
  minimumSpreadToRank: 5,
  minimumDaysToRank: 5,
  minimumReadingsToRank: 20,
  minimumLowDaysToList: 2,
  thresholds: { veryLow: 54, low: 70, tightTargetTop: 140, targetTop: 180, veryHigh: 250 },
  ...overrides,
});

describe("HourlyGlance", () => {
  it("says no hour stood out above the rest when only worst hours are named", async () => {
    // One bad hour among 23 alike: the API names it worst and names no best hour.
    render(HourlyGlance, { props: { report: report({ worstHours: [hour(23, 50)] }) } });

    const bestColumn = page.getByTestId("best-hours");
    await expect.element(bestColumn.getByText(/No hour stood out on its own above the rest/)).toBeVisible();
    await expect.element(bestColumn.getByRole("listitem")).not.toBeInTheDocument();
    await expect.element(page.getByTestId("worst-hours").getByText("50.0%", { exact: true })).toBeVisible();
  });

  it("says no hour stood out below the rest when only best hours are named", async () => {
    render(HourlyGlance, { props: { report: report({ bestHours: [hour(4, 100)] }) } });

    const worstColumn = page.getByTestId("worst-hours");
    await expect.element(worstColumn.getByText(/No hour stood out on its own below the rest/)).toBeVisible();
    await expect.element(worstColumn.getByRole("listitem")).not.toBeInTheDocument();
    await expect.element(page.getByTestId("best-hours").getByText("100.0%", { exact: true })).toBeVisible();
  });

  it("says the hours were too close to call when neither end is named", async () => {
    render(HourlyGlance, {
      props: { report: report({ comparison: HourlyComparison.CloseTogether }) },
    });

    await expect
      .element(page.getByTestId("best-hours").getByText(/all within 5 percentage points/))
      .toBeVisible();
    await expect
      .element(page.getByTestId("worst-hours").getByText(/all within 5 percentage points/))
      .toBeVisible();
  });
});
