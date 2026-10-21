import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import InsulinDonutChart from "./InsulinDonutChart.svelte";

describe("InsulinDonutChart", () => {
  it("prints the backend total and labels each part with the backend value", async () => {
    render(InsulinDonutChart, {
      props: {
        boluses: [{ insulin: 4.5 }, { insulin: 3.25 }],
        totalBolus: 9.9,
        scheduledBasal: 4,
        additionalBasal: 2.35,
        totalInsulin: 16.25,
      },
    });

    await expect.element(page.getByText("16.3U")).toBeVisible();
    await expect.element(page.getByText("Bolus 9.9U")).toBeVisible();
    await expect.element(page.getByText("Scheduled basal 4.0U")).toBeVisible();
    await expect.element(page.getByText("Additional basal 2.4U")).toBeVisible();
  });

  it("draws the arcs from the totals when the backend sends no bolus records", async () => {
    render(InsulinDonutChart, {
      props: {
        boluses: [],
        totalBolus: 7.75,
        scheduledBasal: 4,
        additionalBasal: 2.35,
        totalInsulin: 14.1,
      },
    });

    await expect.element(page.getByText("14.1U")).toBeVisible();
    await expect.element(page.getByText("Bolus 7.8U")).toBeVisible();
  });

  it("shows no total when the backend reports no insulin", async () => {
    render(InsulinDonutChart, {
      props: {
        boluses: [],
        totalBolus: 0,
        scheduledBasal: 0,
        additionalBasal: 0,
        totalInsulin: 0,
      },
    });

    await expect.element(page.getByText("Total:")).not.toBeInTheDocument();
  });
});
