import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, it, expect, vi } from "vitest";

const predictionStatus = vi.hoisted(() => ({
  next: Promise.resolve({ available: false }),
}));
const unavailable = predictionStatus.next;

vi.mock("$api/chart-data.remote", () => ({
  getChartData: vi.fn(async () => (await import("$lib/utils/chart-data-transform")).transformChartData({})),
}));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(() => predictionStatus.next),
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

  afterEach(() => {
    predictionStatus.next = unavailable;
  });

  it.each([
    ["a desktop card", 1440, false],
    ["a phone, where the controls take their own line", 375, true],
  ])(
    "keeps the plot where it was reserved on %s when the plot and the prediction controls arrive",
    async (_name, width, narrow) => {
      await page.viewport(width, 900);
      let reportStatus!: (status: { available: boolean }) => void;
      predictionStatus.next = new Promise((resolve) => (reportStatus = resolve));
      let setDeferred!: (deferred: boolean) => void;
      render(Harness, {
        props: {
          initiallyDeferred: true,
          showPredictions: true,
          narrow,
          onready: (set) => (setDeferred = set),
        },
      });

      const title = page.getByText("Blood Glucose");
      await expect.element(title).toBeVisible();
      const reservedTop = plotArea().getBoundingClientRect().top;
      expect(plotArea().childElementCount).toBe(0);

      reportStatus({ available: true });
      setDeferred(false);
      await vi.waitFor(() => expect(plotArea().childElementCount).toBeGreaterThan(0));
      await expect.element(page.getByText("Cone")).toBeVisible();

      expect(plotArea().getBoundingClientRect().top).toBe(reservedTop);
    }
  );

  describe("the prediction controls' line on a narrow card", () => {
    const controlsSlot = () => page.getByTestId("prediction-controls").element();

    function renderNarrow() {
      let reportStatus!: (status: { available: boolean }) => void;
      predictionStatus.next = new Promise((resolve) => (reportStatus = resolve));
      render(Harness, {
        props: { initiallyDeferred: false, showPredictions: true, narrow: true, onready: () => {} },
      });
      return reportStatus;
    }

    it("is reserved while the prediction status is pending", async () => {
      renderNarrow();

      await expect.element(page.getByText("Blood Glucose")).toBeVisible();
      expect(controlsSlot().getBoundingClientRect().height).toBe(32);
    });

    it("collapses once the status says predictions are unavailable", async () => {
      const reportStatus = renderNarrow();
      await expect.element(page.getByText("Blood Glucose")).toBeVisible();

      reportStatus({ available: false });

      await vi.waitFor(() => expect(controlsSlot().getBoundingClientRect().height).toBe(0));
    });

    it("stays reserved once the status says predictions are available", async () => {
      const reportStatus = renderNarrow();
      await expect.element(page.getByText("Blood Glucose")).toBeVisible();

      reportStatus({ available: true });

      await expect.element(page.getByText("Cone")).toBeVisible();
      expect(controlsSlot().getBoundingClientRect().height).toBeGreaterThanOrEqual(32);
    });
  });
});
