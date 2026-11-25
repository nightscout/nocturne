import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { GlucoseChart, transformChartData } from "@nocturne/charts";
import { glucoseChartLabels } from "./chart-labels.svelte";

const start = Date.UTC(2026, 9, 29, 6);
const HOUR = 60 * 60 * 1000;

/**
 * The package's chart drawn from props alone: no realtime store, no remote function and no
 * app context, as a host outside the app would draw it.
 */
describe("@nocturne/charts GlucoseChart", () => {
  it("draws the plot and the legend from data and labels", async () => {
    const data = transformChartData({
      glucoseData: Array.from({ length: 36 }, (_, i) => ({
        time: start + i * 5 * 60 * 1000,
        sgv: 90 + i * 5,
      })),
      bolusMarkers: [{ time: start + HOUR, insulin: 2.5, treatmentId: "b1" }],
    });

    render(GlucoseChart, {
      data,
      xDomain: { from: new Date(start), to: new Date(start + 3 * HOUR) },
      glucoseUnits: "mg/dl",
      labels: glucoseChartLabels(),
      format: { locale: "en-US", hour12: true },
      heightClass: "h-[400px] w-[600px]",
    });

    await expect.element(page.getByTestId("glucose-plot")).toBeVisible();
    await expect.element(page.getByText("In range")).toBeVisible();
    await expect.element(page.getByText("High", { exact: true })).toBeVisible();
    await expect.element(page.getByText("2.5U")).toBeInTheDocument();
  });
});
