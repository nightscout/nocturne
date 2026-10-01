import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";

const timelinePoints = [
  {
    date: "2025-01-01",
    estimatedA1cPercent: 5.9,
    weightedAverageGlucoseMgdl: 169,
    readingCount: 100,
    daysWithData: 30,
  },
  {
    date: "2025-01-05",
    estimatedA1cPercent: 6.3,
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
    note: "Annual lab test",
  },
  {
    id: "lab-result-2",
    measuredAt: "2025-01-05T12:00:00",
    valuePercent: 6.4,
    note: "Same-day lab test",
  },
  {
    id: "lab-result-before-first-estimate",
    measuredAt: "2024-12-30T12:00:00",
    valuePercent: 6.1,
    note: "Before glucose history",
  },
];

vi.mock("$api/generated/dataOverviews.generated.remote", () => ({
  getAvailableYears: () => remoteQuery(() => ({ years: [2025] })),
  getEHbA1cTimeline: () => remoteQuery(() => ({ points: timelinePoints })),
}));

vi.mock("$api/generated/labHbA1cs.generated.remote", () => ({
  getAll: () => remoteQuery(() => labResults),
  create: remoteCommand(() => undefined),
  remove: remoteCommand(() => undefined),
}));

import EHbA1cPage from "./+page.svelte";

describe("eHbA1c chart tooltips", () => {
  beforeEach(() => {
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

  it("does not extend the estimate line to a lab result before glucose history", async () => {
    await expect.element(page.getByTestId("lab-marker").first()).toBeVisible();
    const line = (await page.getByTestId("ehba1c-line").elements())[0] as SVGPathElement;
    const lineStart = line.getPointAtLength(0);
    const matrix = line.getScreenCTM();
    if (!matrix) throw new Error("eHbA1c curve is not positioned in the chart");
    const screenLineStart = new DOMPoint(lineStart.x, lineStart.y).matrixTransform(matrix);

    const markers = await page.getByTestId("lab-marker").elements();
    const beforeHistoryMarker = markers.find((marker) =>
      marker.querySelector("title")?.textContent?.includes("Before glucose history"),
    ) as SVGPolygonElement | undefined;
    if (!beforeHistoryMarker) throw new Error("Pre-history lab marker is not rendered");
    const markerBounds = beforeHistoryMarker.getBoundingClientRect();

    expect(screenLineStart.x).toBeGreaterThan(markerBounds.right);
  });
});
