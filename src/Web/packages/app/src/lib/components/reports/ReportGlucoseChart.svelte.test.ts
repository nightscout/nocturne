import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";

const { getChartData } = vi.hoisted(() => ({
  getChartData: vi.fn((_window: { startTime: number; endTime: number }) => Promise.resolve(transformed())),
}));

vi.mock("#lib/api/chart-data.remote.js", () => ({ getChartData }));
vi.mock("#lib/api/predictions.remote.js", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import { transformChartData } from "#lib/utils/chart-data-transform.js";
import Harness from "./ReportGlucoseChartHarness.test.svelte";

function transformed() {
  return transformChartData({});
}

const from = new Date(Date.UTC(2026, 9, 19));
const to = new Date(Date.UTC(2026, 9, 20));

describe("ReportGlucoseChart", () => {
  it("reads the window it is given and draws the hosts' marks behind and over the trace", async () => {
    getChartData.mockClear();

    render(Harness, { props: { from, to, insulin: true } });

    await vi.waitFor(() => expect(getChartData.mock.calls.at(-1)?.[0].startTime).toBe(from.getTime()));
    await expect.element(page.getByTestId("under")).toBeInTheDocument();
    await expect.element(page.getByTestId("over")).toBeInTheDocument();
    await expect.element(page.getByText("Basal", { exact: true })).toBeInTheDocument();
    await expect.element(page.getByText("IOB/COB", { exact: true })).toBeInTheDocument();
  });

  it("leaves out the insulin tracks when asked for glucose only", async () => {
    render(Harness, { props: { from, to, insulin: false } });

    await expect.element(page.getByTestId("over")).toBeInTheDocument();
    await expect.element(page.getByText("Basal", { exact: true })).not.toBeInTheDocument();
    await expect.element(page.getByText("IOB/COB", { exact: true })).not.toBeInTheDocument();
  });
});
