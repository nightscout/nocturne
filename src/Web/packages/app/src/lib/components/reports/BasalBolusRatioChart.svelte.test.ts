import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import BasalBolusRatioChart from "./BasalBolusRatioChart.svelte";

// layerchart's BarChart throws under vitest-browser-svelte (as AreaChart does in the AGP test),
// and its marks, axis and tooltip need a live chart. The stubs render the chart's snippets
// against a fixed 0–60 U insulin scale and print what each part receives.
vi.mock("layerchart", async (importOriginal) => {
  const Part = (await import("./ChartPartStub.test.svelte")).default;
  return {
    ...(await importOriginal<typeof import("layerchart")>()),
    BarChart: (await import("./BarChartStub.test.svelte")).default,
    Spline: Part,
    Points: Part,
    Axis: Part,
    Tooltip: { Root: Part, Header: Part, List: Part, Item: Part, Separator: Part },
  };
});

const day = (
  displayDate: string,
  basal: number,
  manualBolus: number,
  carbs?: number,
  automaticBolus = 0,
) => ({
  displayDate,
  basal,
  manualBolus,
  automaticBolus,
  bolus: manualBolus + automaticBolus,
  total: basal + manualBolus + automaticBolus,
  carbs,
});

describe("BasalBolusRatioChart", () => {
  it("draws the carbs on a right-hand axis from zero, scaled to the insulin axis", async () => {
    render(BasalBolusRatioChart, {
      props: {
        data: {
          dailyData: [{ ...day("Oct 1", 30, 28, 180), total: 40 }, day("Oct 2", 21, 12.5, 150)],
        },
      },
    });

    // The 58 U stack, not the day's served total, rounds the insulin axis up to 60 U; 180 g is
    // already a round top for the carbs axis, so it lands on 60 U.
    await expect.element(page.getByTestId("bar-chart")).toHaveAttribute("data-y-domain", "0,60");
    await expect.element(page.getByTestId("axis")).toHaveTextContent("Carbs (g) 180");
    await expect.element(page.getByTestId("mark").first()).toHaveTextContent("60,50");
    await expect.element(page.getByTestId("bar-chart")).toHaveAttribute("data-padding-right", "50");
    await expect.element(page.getByText("Carbs (g)", { exact: true })).toBeVisible();
  });

  it("lists manual and automatic bolus, basal and carbs in the tooltip, then the total", async () => {
    render(BasalBolusRatioChart, {
      props: { data: { dailyData: [day("Oct 1", 20, 15, 180, 2.5)] } },
    });

    await expect.element(page.getByText("Oct 1", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Manual bolus (U): 15.0")).toBeVisible();
    await expect.element(page.getByText("Automatic bolus (U): 2.5")).toBeVisible();
    await expect.element(page.getByText("Basal (U): 20.0")).toBeVisible();
    await expect.element(page.getByText("Carbs (g): 180")).toBeVisible();
    await expect.element(page.getByText("Total (U): 37.5")).toBeVisible();
  });

  it("leaves the carbs out when no day has any", async () => {
    render(BasalBolusRatioChart, {
      props: { data: { dailyData: [day("Oct 1", 20, 15), day("Oct 2", 21, 12.5, 0)] } },
    });

    await expect.element(page.getByText("Total (U): 35.0")).toBeVisible();
    await expect.element(page.getByTestId("axis")).not.toBeInTheDocument();
    await expect.element(page.getByText(/Carbs \(g\)/)).not.toBeInTheDocument();
    await expect.element(page.getByTestId("bar-chart")).toHaveAttribute("data-padding-right", "20");
  });

  it("shows the empty state when there is no insulin", async () => {
    render(BasalBolusRatioChart, {
      props: { data: { dailyData: [day("Oct 1", 0, 0, 120)] } },
    });

    await expect.element(page.getByText("No insulin data available")).toBeVisible();
  });

  it("shows the loading state while the data loads", async () => {
    render(BasalBolusRatioChart, { props: { loading: true } });

    await expect.element(page.getByText("Loading data...")).toBeVisible();
  });
});
