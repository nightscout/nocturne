import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";

vi.mock("$api/chart-data.remote", () => ({
  getChartData: vi.fn(async () => (await import("$lib/utils/chart-data-transform")).transformChartData({})),
}));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import Harness from "./DeferredPlotHarness.test.svelte";

const plotArea = () => page.getByTestId("glucose-plot").element();

describe("GlucoseChartCard deferred drawing", () => {
  it("leaves the plot undrawn while drawing is deferred, and draws it once that ends", async () => {
    let setDeferred!: (deferred: boolean) => void;
    render(Harness, { props: { initiallyDeferred: true, onready: (set) => (setDeferred = set) } });

    await expect.element(page.getByTestId("glucose-plot")).toBeInTheDocument();
    // Long enough for a frame and a task, which is all a release would wait.
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(plotArea().childElementCount).toBe(0);

    setDeferred(false);

    await vi.waitFor(() => expect(plotArea().childElementCount).toBeGreaterThan(0));
  });

  it("draws the plot straight away when nothing defers it", async () => {
    render(Harness, { props: { initiallyDeferred: false, onready: () => {} } });

    await expect.element(page.getByTestId("glucose-plot")).toBeInTheDocument();
    expect(plotArea().childElementCount).toBeGreaterThan(0);
  });
});
