import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import "./app.css";
import ReportView from "./ReportView.svelte";

const from = Date.UTC(2026, 9, 29, 6);
const MINUTE = 60 * 1000;
const readings = 36;
const window = { from, to: from + 3 * 60 * MINUTE };
const glucoseData = Array.from({ length: readings }, (_, i) => ({
  time: from + i * 5 * MINUTE,
  sgv: 90 + i * 5,
  direction: "Flat",
}));

const field = (name: string, label: string, kind: string, unit: string | null, extra = {}) => ({
  name,
  label,
  kind,
  unit,
  description: label,
  ...extra,
});

/** What the API's render_report returns as structuredContent for two panels, cut down. */
const twoPanels = {
  spec: { version: 1, title: "Last three hours" },
  units: "mg/dl",
  summary: "joined",
  warnings: [],
  panels: [
    {
      id: "p1",
      title: "Glucose",
      dataset: "glucose_series",
      primitive: "time_series",
      bindings: { x: "time", y: ["glucose"], band: ["targetLow", "targetHigh"] },
      status: "ok",
      window,
      fields: [
        field("time", "Time", "time", null),
        field("glucose", "Glucose", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true }),
        field("targetLow", "Range low", "number", "mg/dL"),
        field("targetHigh", "Range high", "number", "mg/dL"),
      ],
      rows: glucoseData.map((r) => ({ time: r.time, glucose: r.sgv, targetLow: 70, targetHigh: 180 })),
      thresholds: { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 },
      summary: "Glucose from 2026-10-29 06:00 to 2026-10-29 09:00 UTC.\nReadings: 36.",
      omitted: [],
    },
    {
      id: "p2",
      dataset: "glucose_statistics",
      primitive: "stat_tiles",
      bindings: { columns: ["timeInRangePercent", "mean"] },
      status: "ok",
      window,
      fields: [
        field("timeInRangePercent", "Time in range", "percent", "%"),
        field("mean", "Mean glucose", "number", "mg/dL"),
      ],
      rows: [{ timeInRangePercent: 72.4, mean: 151 }],
      summary: "Glucose statistics from 2026-10-29 06:00.",
    },
  ],
};

/** What show_glucose returns: one dashboard chart panel. */
const chartPanel = (omittedScopes: string[]) => ({
  spec: { version: 1 },
  units: "mg/dl",
  summary: "chart",
  panels: [
    {
      id: "glucose",
      dataset: "glucose_series",
      primitive: "glucose_chart",
      bindings: {},
      status: "ok",
      window,
      fields: [],
      rows: [],
      omittedScopes,
      summary: "Tracks omitted.",
      chart: {
        glucoseData,
        thresholds: { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300 },
        bolusMarkers: [{ time: from + 60 * MINUTE, insulin: 2.5, treatmentId: "b1", bolusType: "Normal" }],
      },
    },
  ],
});

describe("ReportView", () => {
  it("draws two panels, each with its primitive and its summary", async () => {
    render(ReportView, { result: { structuredContent: twoPanels }, locale: "en-US" });

    await expect.element(page.getByRole("heading", { name: "Last three hours" })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Glucose", exact: true })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "glucose_statistics" })).toBeVisible();

    const series = page.getByTestId("report-time-series");
    await expect.element(series).toBeVisible();
    await expect.poll(() => series.element().querySelectorAll("path.fill-none").length).toBeGreaterThanOrEqual(1);

    const tiles = page.getByTestId("report-stat-tiles");
    await expect.element(tiles).toHaveTextContent("Time in range");
    await expect.element(tiles).toHaveTextContent("72.4");
    await expect.element(tiles).toHaveTextContent("151");

    await expect.element(page.getByText("Readings: 36.")).toBeVisible();
    expect(page.getByTestId("report-panel").elements()).toHaveLength(2);
  });

  async function stopColours() {
    const series = page.getByTestId("report-time-series");
    await expect.poll(() => series.element().querySelectorAll("linearGradient stop").length).toBeGreaterThan(0);
    return [...series.element().querySelectorAll("linearGradient stop")].map((s) => s.getAttribute("stop-color") ?? "");
  }

  it("colours a glucose line with the dashboard's spectrum by default", async () => {
    render(ReportView, { result: { structuredContent: twoPanels }, locale: "en-US" });

    const colours = await stopColours();
    expect(colours.every((c) => c.startsWith("oklch("))).toBe(true);
  });

  it("colours a glucose line by range when the report says so", async () => {
    render(ReportView, {
      result: { structuredContent: { ...twoPanels, glucoseColorMode: "threshold" } },
      locale: "en-US",
    });

    const colours = await stopColours();
    expect(colours).toContain("var(--glucose-low)");
    expect(colours).toContain("var(--glucose-in-range)");
  });

  it("places values on the axis once: the low threshold sits at its data value", async () => {
    render(ReportView, { result: { structuredContent: twoPanels }, locale: "en-US" });

    const series = page.getByTestId("report-time-series");
    await expect.poll(() => series.element().querySelector("line.stroke-glucose-low\\/60")).not.toBeNull();
    const rule = series.element().querySelector("line.stroke-glucose-low\\/60")!;
    // 280px tall, 8px top and 24px bottom padding; the axis runs 0 to 1.1 x the highest reading (265).
    const plotHeight = 280 - 8 - 24;
    const expected = plotHeight * (1 - 70 / (265 * 1.1));
    expect(Number(rule.getAttribute("y1"))).toBeCloseTo(expected, 0);
  });

  it("draws the rows of a table panel", async () => {
    const table = {
      ...twoPanels,
      panels: [{ ...twoPanels.panels[0], primitive: "table", bindings: { columns: ["time", "glucose"] }, fields: twoPanels.panels[0].fields.slice(0, 2) }],
    };
    render(ReportView, { result: { structuredContent: table }, locale: "en-US" });

    await expect.element(page.getByRole("columnheader", { name: "Glucose (mg/dL)" })).toBeVisible();
    expect(page.getByRole("row").elements()).toHaveLength(readings + 1);
  });

  it("shows a panel's summary in place of data it could not read", async () => {
    const refused = {
      ...twoPanels,
      panels: [{ ...twoPanels.panels[1], status: "not_permitted", rows: [], summary: "Not shown: the token does not hold the 'glucose.read' scope." }],
    };
    render(ReportView, { result: { structuredContent: refused }, locale: "en-US" });

    await expect.element(page.getByText("Not shown: the token does not hold")).toBeVisible();
    await expect.element(page.getByTestId("report-stat-tiles")).not.toBeInTheDocument();
  });

  it("draws the dashboard chart for show_glucose", async () => {
    render(ReportView, { result: { structuredContent: chartPanel(["heartrate.read"]) }, locale: "en-US" });

    const plot = page.getByTestId("glucose-plot");
    await expect.element(plot).toBeVisible();
    await expect.element(page.getByText("2.5U")).toBeInTheDocument();
    await expect.poll(() => plot.element().querySelectorAll("circle").length).toBeGreaterThanOrEqual(readings);
  });

  it("leaves out the legend toggles of tracks the token could not read", async () => {
    render(ReportView, {
      result: { structuredContent: chartPanel(["treatments.read", "therapy.read"]) },
      locale: "en-US",
    });

    await expect.element(page.getByTestId("glucose-plot")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "IOB" })).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Carbs" })).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Profile" })).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Activity" })).toBeInTheDocument();
  });

  it("waits for the result before drawing", async () => {
    render(ReportView, { result: null, locale: "en-US" });

    await expect.element(page.getByText("Waiting for the report data.")).toBeVisible();
  });

  it("says so when the result carries no report", async () => {
    render(ReportView, { result: { structuredContent: { kind: "other" } }, locale: "en-US" });

    await expect.element(page.getByRole("alert")).toHaveTextContent("no report data");
  });
});
