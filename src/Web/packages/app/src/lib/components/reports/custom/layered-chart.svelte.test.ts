import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { LayeredChart } from "@nocturne/charts";
import { readReport, type ReportPanel } from "@nocturne/charts/core";
import { glucoseChartPanel, scatterPanel } from "#lib/server/report-image/report-image.fixtures.js";

const format = { locale: "en-GB", hour12: false, timeZone: "UTC" };

function panelOf(panel: unknown): ReportPanel {
  const report = readReport({ spec: { version: 1 }, units: "mg/dl", summary: "", warnings: [], panels: [panel] });
  if (!report) throw new Error("The fixture is not a report panel.");
  return report.panels[0];
}

describe("a layered chart on the page", () => {
  it("draws every track, the marks of each layer and the axes of both sides, with a key of the layers", async () => {
    render(LayeredChart, { panel: panelOf(glucoseChartPanel), format });

    const chart = page.getByTestId("report-layered-chart");
    await expect.element(chart).toHaveAttribute("data-tracks", "4");
    const svg = chart.element();
    await expect.poll(() => svg.querySelectorAll('[data-testid="report-layer-line"]').length).toBeGreaterThanOrEqual(3);
    expect(svg.querySelectorAll('[data-testid="report-layer-area"]').length).toBe(1);
    expect(svg.querySelectorAll('[data-testid="report-layer-band"]').length).toBe(1);
    expect(svg.querySelectorAll('[data-testid="report-layer-span"]').length).toBe(2);
    expect(svg.textContent).toContain("40");
    await expect.element(page.getByTestId("report-legend")).toHaveTextContent("COB (g)");
  });

  it("lists each layer's row at the slot under the pointer in one tooltip", async () => {
    render(LayeredChart, { panel: panelOf(glucoseChartPanel), format });
    const chart = page.getByTestId("report-layered-chart");
    await expect.element(chart).toBeVisible();

    const box = chart.element().getBoundingClientRect();
    await userEvent.hover(chart, { position: { x: box.width / 2, y: box.height / 3 } });

    const tooltip = page.getByText("IOB", { exact: true }).last();
    await expect.element(tooltip).toBeVisible();
    const box2 = tooltip.element().closest(".shadow-xl");
    expect(box2, "the tooltip's box").not.toBeNull();
    for (const label of ["Glucose", "Target range", "COB", "Basal", "Pump mode", "Automatic", "mg/dL", " g"])
      expect(box2?.textContent, label).toContain(label);
    // A span's row names its own lane, not the layer that holds every lane.
    expect(box2?.textContent).not.toContain("Modes and overrides");
  });

  it("names the axis of a scatter under it", async () => {
    render(LayeredChart, { panel: panelOf(scatterPanel), format });

    await expect.element(page.getByTestId("report-x-caption")).toHaveTextContent("IOB (U)");
    await expect.poll(() => page.getByTestId("report-layered-chart").element().querySelectorAll('[data-testid="report-layer-point"]').length).toBe(60);
  });

  it("takes the arrow keys as an application, and ends the reading on Escape without closing what holds it", async () => {
    const keysHint = "Use the left and right arrow keys to read the values.";
    render(LayeredChart, { panel: panelOf(glucoseChartPanel), format, keysHint });
    const chart = page.getByRole("application");
    await expect.element(chart).toHaveAccessibleDescription(keysHint);

    let escapesOutside = 0;
    const count = (event: KeyboardEvent) => {
      if (event.key === "Escape") escapesOutside++;
    };
    document.addEventListener("keydown", count);
    try {
      (chart.element() as HTMLElement).focus();
      await userEvent.keyboard("{ArrowRight}");
      await expect.poll(() => document.querySelector('[aria-live="polite"]')?.textContent ?? "").not.toBe("");
      await userEvent.keyboard("{Escape}");
      expect(escapesOutside).toBe(0);
      await expect.poll(() => document.querySelector('[aria-live="polite"]')?.textContent ?? "").toBe("");
      // Not reading, Escape is the page's again: a dialog round the chart closes on it.
      await userEvent.keyboard("{Escape}");
      expect(escapesOutside).toBe(1);
    } finally {
      document.removeEventListener("keydown", count);
    }
  });
});
