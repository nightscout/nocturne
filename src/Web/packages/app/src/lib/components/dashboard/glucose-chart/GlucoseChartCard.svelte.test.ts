import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, vi, afterEach } from "vitest";

vi.mock("#lib/api/chart-data.remote.js", () => ({
  getChartData: vi.fn(async () => (await import("@nocturne/charts/core")).transformChartData({})),
}));
vi.mock("#lib/api/predictions.remote.js", () => ({
  getPredictions: vi.fn(async () => null),
  getPredictionStatus: vi.fn(async () => ({ available: false })),
}));

import { MealPeriod } from "#lib/api/index.js";
import { colorTheme, glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
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
    await userEvent.hover(plot, { position: { x: box.width - 60, y: box.height / 2 } });
    const point = await vi.waitFor(() => {
      const circle = plot.element().querySelector<SVGCircleElement>(".lc-highlight-point");
      expect(circle).not.toBeNull();
      return circle!;
    });
    await userEvent.click(point);

    await expect.element(dialog).toBeVisible();
    await expect.element(dialog.getByText("120")).toBeVisible();
  });

  it("drops its chrome below @2xl without redrawing the plot", async () => {
    const { rerender } = render(Harness, { props: { initiallyDeferred: false, onready: () => {} } });

    const plot = page.getByTestId("glucose-plot");
    await vi.waitFor(() => expect(plot.element().querySelector("svg")).not.toBeNull());
    const svg = plot.element().querySelector("svg");
    const card = plot.element().closest<HTMLElement>('[data-slot="card"]')!;
    expect(getComputedStyle(card).borderTopWidth).toBe("1px");

    await rerender({ narrow: true });

    await vi.waitFor(() => expect(getComputedStyle(card).borderTopWidth).toBe("0px"));
    expect(card.isConnected).toBe(true);
    expect(plot.element().querySelector("svg")).toBe(svg);
  });

  it("words the meal period of a carb treatment in the inspection dialog", async () => {
    render(Harness, {
      props: {
        initiallyDeferred: false,
        carbMarkers: [
          { time: Date.now() - 8 * 60 * 1000, carbs: 40, mealPeriod: MealPeriod.LateNight },
        ],
        onready: () => {},
      },
    });

    const plot = page.getByTestId("glucose-plot");
    await vi.waitFor(() => expect(plot.element().querySelector("svg")).not.toBeNull());

    const box = plot.element().getBoundingClientRect();
    await userEvent.hover(plot, { position: { x: box.width - 60, y: box.height / 2 } });
    const point = await vi.waitFor(() => {
      const circle = plot.element().querySelector<SVGCircleElement>(".lc-highlight-point");
      expect(circle).not.toBeNull();
      return circle!;
    });
    await userEvent.click(point);

    await page.getByText("Treatment", { exact: true }).click();

    await expect
      .element(page.getByRole("dialog").getByText("40g Late night", { exact: true }))
      .toBeVisible();
  });

  it("draws its axis ticks and inspection dialog in the units it is given, not the viewer's", async () => {
    glucoseUnits.current = "mg/dl";
    render(Harness, {
      props: { initiallyDeferred: false, glucoseUnits: "mmol", onready: () => {} },
    });

    const plot = page.getByTestId("glucose-plot");
    await vi.waitFor(() => {
      const ticks = [...plot.element().querySelectorAll(".lc-axis-tick-label")].map(
        (tick) => tick.textContent?.trim() ?? ""
      );
      expect(ticks.some((tick) => /^\d+\.\d$/.test(tick))).toBe(true);
    });

    const box = plot.element().getBoundingClientRect();
    await userEvent.hover(plot, { position: { x: box.width - 60, y: box.height / 2 } });
    const point = await vi.waitFor(() => {
      const circle = plot.element().querySelector<SVGCircleElement>(".lc-highlight-point");
      expect(circle).not.toBeNull();
      return circle!;
    });
    await userEvent.click(point);

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("6.7").first()).toBeVisible();
    expect(dialog.element().textContent).not.toMatch(/(?<!\d)120(?!\d)/);
  });
});

describe("GlucoseChartCard in the Nightscout theme", () => {
  afterEach(() => {
    colorTheme.current = "nocturne";
  });

  function chartHeights(frame: HTMLElement) {
    return [
      frame.style.getPropertyValue("--ns-focus-height"),
      frame.style.getPropertyValue("--ns-context-height"),
    ];
  }

  /** index.js: the window below the chart's top, less a 10px margin. */
  function expectedHeights(frame: HTMLElement) {
    const available = Math.max(
      360,
      window.innerHeight - (frame.getBoundingClientRect().top + window.scrollY) - 10
    );
    return [`${Math.round(available * 0.7)}px`, `${Math.round(available * 0.3)}px`];
  }

  async function renderNightscoutChart() {
    colorTheme.current = "nightscout";
    render(Harness, { props: { initiallyDeferred: false, onready: () => {} } });
    return vi.waitFor(() => {
      const el = page.getByTestId("nightscout-chart").element() as HTMLElement;
      expect(el).not.toBeNull();
      return el;
    });
  }

  it("gives the window below the chart's top 70% to the focus chart and 30% to the context chart", async () => {
    const frame = await renderNightscoutChart();

    await vi.waitFor(() => expect(chartHeights(frame)).toEqual(expectedHeights(frame)));
    const plot = page.getByTestId("glucose-plot").element();
    expect(plot.closest('[data-slot="card"]')).toBeNull();
  });

  it("measures again when the content above the chart changes height", async () => {
    const spacer = document.createElement("div");
    document.body.prepend(spacer);
    try {
      const frame = await renderNightscoutChart();
      await vi.waitFor(() => expect(chartHeights(frame)).toEqual(expectedHeights(frame)));
      const before = chartHeights(frame);

      spacer.style.height = "100px";

      await vi.waitFor(() => {
        expect(chartHeights(frame)).toEqual(expectedHeights(frame));
        expect(chartHeights(frame)).not.toEqual(before);
      });
    } finally {
      spacer.remove();
    }
  });
});
