import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";

const createLab = vi.hoisted(() => vi.fn(() => undefined));

const timelinePoints = [
  {
    date: "2025-01-01",
    estimatedA1cPercent: 5.9,
    a1cDisplay: { percent: 5.9, mmolMol: 40.98375 },
    weightedAverageGlucoseMgdl: 169,
    readingCount: 100,
    daysWithData: 30,
  },
  {
    date: "2025-01-05",
    estimatedA1cPercent: 6.3,
    a1cDisplay: { percent: 6.3, mmolMol: 45.35535 },
    weightedAverageGlucoseMgdl: 180,
    readingCount: 100,
    daysWithData: 30,
  },
];

const labResults = [
  {
    id: "lab-result-1",
    measuredAt: "2025-01-03T12:00:00",
    valuePercent: 6.6,
    a1cDisplay: { percent: 6.6, mmolMol: 48.63405 },
    note: "Annual lab test",
  },
  {
    id: "lab-result-2",
    measuredAt: "2025-01-05T12:00:00",
    valuePercent: 6.4,
    a1cDisplay: { percent: 6.4, mmolMol: 46.44825 },
    note: "Same-day lab test",
  },
  {
    id: "lab-result-before-first-estimate",
    measuredAt: "2024-12-30T12:00:00",
    valuePercent: 6.1,
    a1cDisplay: { percent: 6.1, mmolMol: 43.16955 },
    note: "Before glucose history",
  },
];

vi.mock("$api/generated/dataOverviews.generated.remote", () => ({
  getAvailableYears: () => remoteQuery(() => ({ years: [2025] })),
  getEHbA1cTimeline: () =>
    remoteQuery(() => ({
      points: timelinePoints,
      a1cReferences: {
        minimum: { percent: 4, mmolMol: 20.21865 },
        healthy: { percent: 5.7, mmolMol: 38.79795 },
        target: { percent: 7, mmolMol: 53.00565 },
        elevated: { percent: 9, mmolMol: 74.86365 },
        veryHigh: { percent: 14, mmolMol: 129.50865 },
        mmolMolPadding: 5.4645,
      },
    })),
}));

vi.mock("$api/generated/labHbA1cs.generated.remote", () => ({
  getAll: () => remoteQuery(() => labResults),
  create: remoteCommand(createLab),
  remove: remoteCommand(() => undefined),
}));

import { applyPreferences } from "$lib/stores/appearance-store.svelte";

import EHbA1cPage from "./+page.svelte";

describe("eHbA1c chart tooltips", () => {
  beforeEach(() => {
    createLab.mockClear();
    applyPreferences({ a1cName: "HbA1c", a1cUnits: "percent" });
    render(EHbA1cPage, {});
  });

  it("labels a hovered lab marker as Lab result and the curve as eHbA1c", async () => {
    await expect.element(page.getByTestId("lab-marker").first()).toBeVisible();
    const chart = page.getByTestId("ehba1c-chart");
    const chartElement = (await chart.elements())[0] as HTMLElement;
    const chartBounds = chartElement.getBoundingClientRect();

    const marker = (
      await page.getByTestId("lab-marker").elements()
    )[0] as SVGPolygonElement;
    const markerBounds = marker.getBoundingClientRect();
    await userEvent.hover(chart, {
      position: {
        x: markerBounds.x - chartBounds.x + markerBounds.width / 2,
        y: markerBounds.y - chartBounds.y + markerBounds.height / 2,
      },
    });
    const tooltip = page.getByTestId("ehba1c-tooltip");
    await expect.element(tooltip).toBeVisible();
    let tooltipText = (await tooltip.elements())[0].textContent ?? "";
    expect(tooltipText).toContain("Lab result");
    expect(tooltipText).toContain("Annual lab test");
    expect(tooltipText).not.toContain("eHbA1c");

    const line = (
      await page.getByTestId("ehba1c-line").elements()
    )[0] as SVGPathElement;
    const screenLineEnd = line.getPointAtLength(line.getTotalLength() - 1);
    const matrix = line.getScreenCTM();
    if (!matrix) throw new Error("eHbA1c curve is not positioned in the chart");
    const linePoint = new DOMPoint(
      screenLineEnd.x,
      screenLineEnd.y
    ).matrixTransform(matrix);

    await userEvent.hover(chart, {
      position: {
        x: linePoint.x - chartBounds.x,
        y: linePoint.y - chartBounds.y,
      },
    });
    await expect.element(tooltip).toBeVisible();
    tooltipText = (await tooltip.elements())[0].textContent ?? "";
    expect(tooltipText).toContain("eHbA1c");
    expect(tooltipText).toContain("Lab result");
    expect(tooltipText).toContain("Same-day lab test");
  });

  it("uses global names and units while preserving the estimate prefix and lab distinction", async () => {
    applyPreferences({ a1cName: "A1c", a1cUnits: "mmol/mol" });
    await expect.element(page.getByTestId("lab-marker").first()).toBeVisible();
    await expect
      .element(page.getByText("Estimated A1c (eA1c)", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByLabelText("Result (mmol/mol)"))
      .toBeVisible();
    await expect.element(page.getByTestId("lab-marker").first()).toHaveTextContent("49 mmol/mol");
    await expect.element(page.getByRole("group", { name: /A1c units/i })).not.toBeInTheDocument();
  });

  it("submits the entered unit to the backend and clears drafts when units change", async () => {
    await expect.element(page.getByTestId("lab-marker").first()).toBeVisible();
    await page.getByLabelText("Result (%)").fill("7");
    applyPreferences({ a1cUnits: "mmol/mol" });
    await expect
      .element(page.getByLabelText("Result (mmol/mol)"))
      .toHaveValue(null);
    await page.getByLabelText("Date of blood draw").fill("2025-02-01");
    await page.getByLabelText("Result (mmol/mol)").fill("53");
    await page.getByRole("button", { name: "Add lab result" }).click();
    expect(createLab).toHaveBeenCalledWith(
      expect.objectContaining({ value: 53, unit: "mmol/mol" })
    );
  });

  it("does not extend the estimate line to a lab result before glucose history", async () => {
    await expect.element(page.getByTestId("lab-marker").first()).toBeVisible();
    const line = (
      await page.getByTestId("ehba1c-line").elements()
    )[0] as SVGPathElement;
    const lineStart = line.getPointAtLength(0);
    const matrix = line.getScreenCTM();
    if (!matrix) throw new Error("eHbA1c curve is not positioned in the chart");
    const screenLineStart = new DOMPoint(
      lineStart.x,
      lineStart.y
    ).matrixTransform(matrix);

    const markers = await page.getByTestId("lab-marker").elements();
    const beforeHistoryMarker = markers.find((marker) =>
      marker
        .querySelector("title")
        ?.textContent?.includes("Before glucose history")
    ) as SVGPolygonElement | undefined;
    if (!beforeHistoryMarker)
      throw new Error("Pre-history lab marker is not rendered");
    const markerBounds = beforeHistoryMarker.getBoundingClientRect();

    expect(screenLineStart.x).toBeGreaterThan(markerBounds.right);
  });
});
