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
    for (const label of ["Glucose", "Target range", "COB", "Basal", "Modes and overrides", "Automatic", "mg/dL", " g"])
      expect(box2?.textContent, label).toContain(label);
  });

  it("names the axis of a scatter under it", async () => {
    render(LayeredChart, { panel: panelOf(scatterPanel), format });

    await expect.element(page.getByTestId("report-x-caption")).toHaveTextContent("IOB (U)");
    await expect.poll(() => page.getByTestId("report-layered-chart").element().querySelectorAll('[data-testid="report-layer-point"]').length).toBe(60);
  });
});
