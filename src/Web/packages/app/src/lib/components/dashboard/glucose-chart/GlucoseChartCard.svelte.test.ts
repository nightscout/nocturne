import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";

vi.mock("$api/chart-data.remote", () => ({
  getChartData: vi.fn(async () => (await import("$lib/utils/chart-data-transform")).transformChartData({})),
}));
vi.mock("$api/predictions.remote", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import Harness from "./DeferredPlotHarness.test.svelte";

describe("GlucoseChartCard inspection dialogs", () => {
  it("loads the glucose inspection dialog when a glucose point is first clicked", async () => {
    render(Harness, { props: { initiallyDeferred: false, onready: () => {} } });

    const plot = page.getByTestId("glucose-plot");
    await vi.waitFor(() => expect(plot.element().querySelector("svg")).not.toBeNull());

    const dialog = page.getByRole("dialog");
    expect(dialog.elements()).toHaveLength(0);

    // The harness's readings sit at the right edge of the window; hovering there highlights the latest.
    const box = plot.element().getBoundingClientRect();
    await userEvent.hover(plot, { position: { x: box.width - 40, y: box.height / 2 } });
    const point = await vi.waitFor(() => {
      const circle = plot.element().querySelector<SVGCircleElement>(".lc-highlight-point");
      expect(circle).not.toBeNull();
      return circle!;
    });
    await userEvent.click(point);

    await expect.element(dialog).toBeVisible();
    await expect.element(dialog.getByText("120")).toBeVisible();
  });
});
