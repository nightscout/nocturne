import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getChartData } from "$api/chart-data.remote";
import { getPredictions, getPredictionStatus } from "$api/predictions.remote";
import { getMultiPeriodStatistics } from "$api/generated/statistics.generated.remote";
import { getCurrentBatteryStatus } from "$api/generated/batteries.generated.remote";
import Demo from "./DashboardDemoHarness.test.svelte";

vi.mock("$api/chart-data.remote", () => ({ getChartData: vi.fn() }));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(),
  getPredictionStatus: vi.fn(),
}));
vi.mock("$api/generated/statistics.generated.remote", () => ({
  getMultiPeriodStatistics: vi.fn(),
}));
vi.mock("$api/generated/batteries.generated.remote", () => ({
  getCurrentBatteryStatus: vi.fn(),
}));

const allowedSettings = [
  "/settings/appearance",
  "/settings/connectors",
  "/settings/profile",
  "/settings/trackers",
];
beforeEach(() => {
  vi.clearAllMocks();
});

describe("dashboard documentation demo", () => {
  it("fills every registered widget without requesting patient data", async () => {
    render(Demo, { allowedSettings });
    for (const title of [
      "BG Delta",
      "Last Updated",
      "Connection",
      "Recent Meals",
      "Trackers",
      "Time in Range",
      "Today's Summary",
      "Clock",
      "Total Daily Dose",
    ]) {
      await expect
        .element(
          page.getByRole("button", {
            name: `View explanation: ${title}`,
            exact: true,
          })
        )
        .toBeVisible();
    }
    await expect
      .element(
        page.getByRole("heading", { name: "Total Daily Dose", exact: true })
      )
      .toBeVisible();
    await expect.element(page.getByText("42.0", { exact: true })).toBeVisible();
    expect(getChartData).not.toHaveBeenCalled();
    expect(getPredictions).not.toHaveBeenCalled();
    expect(getPredictionStatus).not.toHaveBeenCalled();
    expect(getMultiPeriodStatistics).not.toHaveBeenCalled();
    expect(getCurrentBatteryStatus).not.toHaveBeenCalled();
  });

  it("opens only one explanation, switches complete examples and restores the full demo on close", async () => {
    render(Demo, { allowedSettings });
    await page
      .getByRole("button", {
        name: "View explanation: Total Daily Dose",
        exact: true,
      })
      .click();
    const explanation = page.getByRole("region", {
      name: "Widget explanation",
    });
    await page
      .getByRole("button", { name: "Without automatic boluses", exact: true })
      .click();
    await expect.element(page.getByText("38.0", { exact: true })).toBeVisible();
    await expect
      .element(
        explanation.getByRole("link", { name: "Appearance", exact: true })
      )
      .toHaveAttribute("target", "_blank");
    await page
      .getByRole("button", {
        name: "View explanation: Recent Meals",
        exact: true,
      })
      .click();
    await expect
      .element(
        explanation.getByRole("heading", { name: "Recent Meals", exact: true })
      )
      .toBeVisible();
    expect(explanation.elements()).toHaveLength(1);
    await page
      .getByRole("button", { name: "Missing source data", exact: true })
      .click();
    await expect
      .element(page.getByText("No recent meals", { exact: true }))
      .toBeVisible();
    await page
      .getByRole("button", { name: "Close explanation", exact: true })
      .click();
    await expect.element(page.getByText("75g", { exact: true })).toBeVisible();
    expect(explanation.elements()).toHaveLength(0);
  });

  it("keeps graph practice separate from help and retains preferences", async () => {
    const { glucoseChartLookback, predictionDisplayMode } =
      await import("$lib/stores/appearance-store.svelte");
    const lookback = glucoseChartLookback.current;
    const mode = predictionDisplayMode.current;
    render(Demo, { allowedSettings: [] });
    await page.getByRole("button", { name: "3 hours", exact: true }).click();
    expect(
      page.getByRole("region", { name: "Widget explanation" }).elements()
    ).toHaveLength(0);
    await page
      .getByRole("button", { name: "Glucose line and points", exact: true })
      .click();
    await expect
      .element(page.getByRole("region", { name: "Widget explanation" }))
      .toBeVisible();
    expect(page.getByRole("link").elements()).toHaveLength(0);
    expect(glucoseChartLookback.current).toBe(lookback);
    expect(predictionDisplayMode.current).toBe(mode);
    expect(getChartData).not.toHaveBeenCalled();
  });

  it("draws and switches example forecasts even when personal predictions are disabled", async () => {
    const { predictionEnabled, predictionDisplayMode, predictionMinutes } = await import("$lib/stores/appearance-store.svelte");
    const saved = { enabled: predictionEnabled.current, mode: predictionDisplayMode.current, minutes: predictionMinutes.current };
    try {
      predictionEnabled.current = false;
      predictionDisplayMode.current = "cone";
      predictionMinutes.current = 15;
      render(Demo, { allowedSettings });
      const plot = page.getByTestId("glucose-plot");
      await vi.waitFor(() => expect(plot.element().querySelector('[class~="stroke-pred-main"]')).not.toBeNull());
      await page.getByRole("button", { name: "Lines", exact: true }).click();
      await vi.waitFor(() => expect(plot.element().querySelector('[class~="stroke-pred-iob"]')).not.toBeNull());
      expect(predictionEnabled.current).toBe(false);
      expect(predictionDisplayMode.current).toBe("cone");
      expect(predictionMinutes.current).toBe(15);
      expect(getPredictionStatus).not.toHaveBeenCalled();
      expect(getPredictions).not.toHaveBeenCalled();
    } finally {
      predictionEnabled.current = saved.enabled;
      predictionDisplayMode.current = saved.mode;
      predictionMinutes.current = saved.minutes;
    }
  });

  it("places help directly below the selected widget in a narrow panel", async () => {
    render(Demo, { allowedSettings, width: 360 });
    const button = page.getByRole("button", { name: "View explanation: BG Delta", exact: true });
    await button.click();
    const explanation = page.getByRole("region", { name: "Widget explanation" });
    await vi.waitFor(() => {
      const row = button.element().closest('[data-slot="card"]')?.parentElement;
      expect(row?.children).toHaveLength(1);
      expect(row?.nextElementSibling).toBe(explanation.element());
    });
  });

  it("reconnects the local example without touching the patient connection", async () => {
    render(Demo, { allowedSettings });
    await page.getByRole("button", { name: "View explanation: Connection", exact: true }).click();
    await page.getByRole("button", { name: "No live connection", exact: true }).click();
    await expect.element(page.getByText("Disconnected", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Retry", exact: true }).click();
    await expect.element(page.getByText("Connected", { exact: true })).toBeVisible();
    expect(getChartData).not.toHaveBeenCalled();
  });

  it("shows complete glucose examples and neutralises an older reading", async () => {
    render(Demo, { allowedSettings });
    await page
      .getByRole("button", { name: "Reading age", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Older measurement", exact: true })
      .click();
    await expect
      .element(page.getByText("15 min ago", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("active arrow is hidden");
    await page
      .getByRole("button", { name: "No supplied trend", exact: true })
      .click();
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("does not invent an arrow");
  });
});
