import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import "./app.css";
import ReportView from "./ReportBody.svelte";

const from = Date.UTC(2026, 9, 29, 6);
const MINUTE = 60 * 1000;
const window = { from, to: from + 3 * 60 * MINUTE };

const field = (name: string, label: string, kind: string, unit: string | null, extra = {}) => ({
  name,
  label,
  kind,
  unit,
  description: label,
  ...extra,
});

const report = (panels: unknown[], title = "Report") => ({
  spec: { version: 1, title },
  units: "mg/dl",
  summary: "joined",
  warnings: [],
  panels,
});

/** What daily_summary returns drawn as a bar: three days, two fields side by side. */
const dailyBar = (stacked: boolean) => ({
  id: "days",
  dataset: "daily_summary",
  primitive: "bar",
  bindings: { x: "date", y: ["timeBelow", "timeAbove"], stacked },
  status: "ok",
  window,
  fields: [
    field("date", "Date", "category", null),
    field("timeBelow", "Below range", "percent", "%", { color: "glucose-low" }),
    field("timeAbove", "Above range", "percent", "%", { color: "glucose-high" }),
  ],
  rows: [
    { date: "2026-10-27", timeBelow: 2, timeAbove: 20 },
    { date: "2026-10-28", timeBelow: 4, timeAbove: 30 },
    { date: "2026-10-29", timeBelow: 1, timeAbove: 25 },
  ],
  summary: "Daily summary over local days 2026-10-27 to 2026-10-29.",
});

/** What glucose_distribution returns drawn as a bar: each row carries its band's colour. */
const distribution = {
  id: "ranges",
  title: "Time in each glucose range",
  dataset: "glucose_distribution",
  primitive: "bar",
  bindings: { x: "band", y: ["percent"], stacked: false },
  status: "ok",
  window,
  fields: [
    field("band", "Band", "category", null, { colorField: "color" }),
    field("percent", "Time", "percent", "%"),
    field("color", "Colour", "category", null),
  ],
  rows: [
    { band: "Very low", percent: 0.4, color: "glucose-very-low" },
    { band: "Low", percent: 1.6, color: "glucose-low" },
    { band: "In range", percent: 71.8, color: "glucose-in-range" },
    { band: "High", percent: 20.9, color: "glucose-high" },
    { band: "Very high", percent: 5.3, color: "glucose-very-high" },
  ],
  summary: "Time in each band: very low (below 54 mg/dL) 0.4%, ...",
};

const glucoseRows = Array.from({ length: 36 }, (_, i) => ({ time: from + i * 5 * MINUTE, glucose: 110 + i * 3, targetLow: 70, targetHigh: 180 }));

/** What show_treatments' first panel returns: a glucose line with the panel's markers. */
const markedLine = {
  id: "glucose",
  title: "Glucose with boluses and carbs",
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
  rows: glucoseRows,
  thresholds: { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 },
  markers: [
    { time: from + 30 * MINUTE, kind: "bolus", amount: 4.5, automatic: false },
    { time: from + 30 * MINUTE, kind: "carbs", amount: 45 },
    { time: from + 90 * MINUTE, kind: "bolus", amount: 0.3, automatic: true },
  ],
  summary: "Markers drawn: 2 boluses, 1 carb entry.",
};

/** show_agp's result for a tenant, cut down: the figures, the hourly bands and the distribution bar. */
const agp = report(
  [
    {
      id: "figures",
      title: "Key figures",
      dataset: "glucose_statistics",
      primitive: "stat_tiles",
      bindings: { columns: ["timeInRangePercent", "mean", "gmi", "cv", "timeBelow", "timeAbove"] },
      status: "ok",
      window,
      fields: [
        field("timeInRangePercent", "Time in range", "percent", "%"),
        field("mean", "Mean glucose", "number", "mg/dL"),
        field("gmi", "GMI", "number", "%"),
        field("cv", "CV", "percent", "%"),
        field("timeBelow", "Time below range", "percent", "%"),
        field("timeAbove", "Time above range", "percent", "%"),
      ],
      rows: [{ timeInRangePercent: 71.8, mean: 152, gmi: 6.9, cv: 34.1, timeBelow: 2, timeAbove: 26.2 }],
      omitted: [
        { field: "tdd", scope: "treatments.read" },
        { field: "basalPercent", scope: "treatments.read" },
        { field: "carbsPerDay", scope: "treatments.read" },
      ],
      summary: "Glucose statistics.\nFields omitted: tdd, basalPercent, carbsPerDay (scope not granted).",
    },
    {
      id: "profile",
      title: "Glucose pattern over the day",
      dataset: "glucose_distribution_by_hour",
      primitive: "range_bands",
      bindings: { x: "hour", y: ["glucoseP50"], band: ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"] },
      status: "ok",
      window,
      fields: [
        field("hour", "Hour", "time_of_day", "min"),
        field("glucoseP50", "Glucose median", "number", "mg/dL", { color: "glucose-in-range" }),
        field("glucoseP10", "Glucose P10", "number", "mg/dL"),
        field("glucoseP25", "Glucose P25", "number", "mg/dL"),
        field("glucoseP75", "Glucose P75", "number", "mg/dL"),
        field("glucoseP90", "Glucose P90", "number", "mg/dL"),
      ],
      rows: Array.from({ length: 24 }, (_, h) => {
        const m = 130 + 30 * Math.sin((h / 24) * 2 * Math.PI);
        return { hour: h * 60, glucoseP50: m, glucoseP10: m - 45, glucoseP25: m - 20, glucoseP75: m + 25, glucoseP90: m + 60 };
      }),
      thresholds: { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 },
      summary: "Glucose by hour of day.",
    },
    distribution,
  ],
  "Ambulatory glucose profile, last 14 days",
);

describe("bar primitive", () => {
  it("draws two fields side by side in their own colours, one bar each per category", async () => {
    render(ReportView, { result: { structuredContent: report([dailyBar(false)]) }, locale: "en-US" });

    const bar = page.getByTestId("report-bar");
    await expect.element(bar).toBeVisible();
    await expect.poll(() => bar.element().querySelectorAll("[data-testid=report-bar-segment]").length).toBe(6);
    const segments = [...bar.element().querySelectorAll("[data-testid=report-bar-segment]")];
    expect(new Set(segments.map((s) => s.getAttribute("data-color")))).toEqual(new Set(["var(--glucose-low)", "var(--glucose-high)"]));
    const [below, above] = segments.filter((s) => s.getAttribute("data-category") === "2026-10-28");
    expect(Number(above.getAttribute("x"))).toBeGreaterThan(Number(below.getAttribute("x")));
    await expect.element(page.getByText("Below range (%)")).toBeVisible();
    await expect.element(page.getByText("Above range (%)")).toBeVisible();
  });

  it("stacks the fields when the panel says so", async () => {
    render(ReportView, { result: { structuredContent: report([dailyBar(true)]) }, locale: "en-US" });

    const bar = page.getByTestId("report-bar");
    await expect.poll(() => bar.element().querySelectorAll("[data-testid=report-bar-segment]").length).toBe(6);
    const [below, above] = [...bar.element().querySelectorAll("[data-testid=report-bar-segment][data-category='2026-10-28']")];
    expect(above.getAttribute("x")).toBe(below.getAttribute("x"));
    expect(Number(above.getAttribute("y")) + Number(above.getAttribute("height"))).toBeCloseTo(Number(below.getAttribute("y")), 1);
  });

  it("colours each glucose band's bar from the colour its row carries", async () => {
    render(ReportView, { result: { structuredContent: report([distribution]) }, locale: "en-US" });

    const bar = page.getByTestId("report-bar");
    await expect.poll(() => bar.element().querySelectorAll("[data-testid=report-bar-segment]").length).toBe(5);
    expect([...bar.element().querySelectorAll("[data-testid=report-bar-segment]")].map((s) => s.getAttribute("data-color"))).toEqual([
      "var(--glucose-very-low)",
      "var(--glucose-low)",
      "var(--glucose-in-range)",
      "var(--glucose-high)",
      "var(--glucose-very-high)",
    ]);
  });
});

describe("time_series markers", () => {
  it("draws the panel's boluses and carbs as the dashboard's markers on the glucose line", async () => {
    render(ReportView, { result: { structuredContent: report([markedLine]) }, locale: "en-US" });

    const series = page.getByTestId("report-time-series");
    await expect.poll(() => series.element().querySelectorAll("[data-testid=report-bolus-marker]").length).toBe(2);
    expect(series.element().querySelectorAll("[data-testid=report-carb-marker]")).toHaveLength(1);
    await expect.element(page.getByText("4.5U")).toBeInTheDocument();
    await expect.element(page.getByText("45g")).toBeInTheDocument();
    const automatic = series.element().querySelectorAll("[data-testid=report-bolus-marker] polygon[fill=none]");
    expect(automatic).toHaveLength(1);
  });

  it("draws a treatments panel's markers at their amounts, the algorithm's outlined as the API says, and no line", async () => {
    const insulin = {
      ...markedLine,
      id: "doses",
      dataset: "treatments",
      bindings: { x: "time", y: ["insulin"] },
      fields: [field("time", "Time", "time", null), field("insulin", "Insulin", "number", "U", { color: "insulin-bolus", marker: "bolus" })],
      rows: [
        { time: from + 20 * MINUTE, insulin: 3 },
        { time: from + 70 * MINUTE, insulin: null },
        { time: from + 100 * MINUTE, insulin: 0.3 },
      ],
      // The rows carry no automatic flag: only the API's markers say which bolus the algorithm gave.
      markers: [
        { time: from + 20 * MINUTE, kind: "bolus", amount: 3, automatic: false },
        { time: from + 100 * MINUTE, kind: "bolus", amount: 0.3, automatic: true },
      ],
    };
    render(ReportView, { result: { structuredContent: report([insulin]) }, locale: "en-US" });

    const series = page.getByTestId("report-time-series");
    await expect.poll(() => series.element().querySelectorAll("[data-testid=report-bolus-marker]").length).toBe(2);
    expect(series.element().querySelectorAll("path.fill-none")).toHaveLength(0);
    expect(series.element().querySelectorAll("[data-testid=report-bolus-marker] polygon[fill=none]")).toHaveLength(1);
    await expect.element(page.getByText("0.3U")).toBeInTheDocument();
  });
});

describe("show_agp", () => {
  it("draws the figures, the hourly bands and the band bar, with each summary", async () => {
    render(ReportView, { result: { structuredContent: agp }, locale: "en-US" });

    await expect.element(page.getByRole("heading", { name: "Ambulatory glucose profile, last 14 days" })).toBeVisible();
    expect(page.getByTestId("report-panel").elements()).toHaveLength(3);
    await expect.element(page.getByTestId("report-stat-tiles")).toHaveTextContent("71.8");
    await expect.element(page.getByTestId("report-stat-tiles")).not.toHaveTextContent("Insulin per day");
    await expect.element(page.getByTestId("report-range-bands")).toBeVisible();
    await expect.poll(() => page.getByTestId("report-bar-segment").elements().length).toBe(5);
    await expect.element(page.getByText("Fields omitted: tdd, basalPercent, carbsPerDay (scope not granted).")).toBeVisible();
  });
});
