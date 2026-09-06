import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import TIRStackedChart from "./TIRStackedChart.svelte";

describe("TIRStackedChart", () => {
  it("renders a tight-range inset within the target segment (horizontal)", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, tightTarget: 40, high: 15, veryHigh: 10 },
        orientation: "horizontal",
      },
    });

    await expect.element(page.getByTestId("tight-range-inset")).toBeVisible();
  });

  it("omits the inset when tightTarget is absent", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, high: 15, veryHigh: 10 },
        orientation: "horizontal",
      },
    });

    await expect.element(page.getByTestId("tight-range-inset")).not.toBeInTheDocument();
  });

  it("renders a tight-range inset within the target segment (vertical)", async () => {
    render(TIRStackedChart, {
      props: {
        percentages: { veryLow: 2, low: 3, target: 70, tightTarget: 40, high: 15, veryHigh: 10 },
        orientation: "vertical",
      },
    });

    // The stacked bar's "In Range" percentage label still renders on-chart.
    await expect.element(page.getByText("70%").first()).toBeVisible();
    // The layerchart Chart measures its container via ResizeObserver, which reports
    // zero height in this unsized test harness — pixel geometry is covered by manual
    // verification against a real report page instead. This asserts the conditional
    // render logic fires: the inset exists in the DOM whenever tightTarget is present.
    await expect.element(page.getByTestId("tight-range-inset")).toBeInTheDocument();
  });
});
