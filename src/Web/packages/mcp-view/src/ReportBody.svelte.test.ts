import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import "./app.css";
import ReportView from "./ReportBody.svelte";
import { cohortBands } from "@nocturne/charts/core";

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

  /** An event_response result: two cohorts' bands, and the events as a table. */
  const minutes = Array.from({ length: 24 }, (_, i) => -60 + i * 15);
  const slotRows = (series: string, cohort: number, lift: number) =>
    minutes.map((minute) => {
      const rise = Math.max(0, Math.min(minute, 60)) + lift;
      return {
        series, cohort, range: "primary", minute,
        glucoseP10: 90 + rise, glucoseP25: 100 + rise, glucoseP50: 115 + rise, glucoseP75: 130 + rise, glucoseP90: 150 + rise,
        sentence: `${series}, ${minute} minutes from the event: median ${115 + rise} mg/dL.`,
      };
    });
  const bandFields = [
    field("series", "Series", "category", null),
    field("minute", "Minutes from event", "offset", "min"),
    field("glucoseP50", "Glucose median", "number", "mg/dL"),
    field("glucoseP10", "Glucose P10", "number", "mg/dL"),
    field("glucoseP25", "Glucose P25", "number", "mg/dL"),
    field("glucoseP75", "Glucose P75", "number", "mg/dL"),
    field("glucoseP90", "Glucose P90", "number", "mg/dL"),
    field("sentence", "Sentence", "category", null),
  ];
  const sentence = "Meal of 45 g on 2026-10-12 at 08:05: started at 110 mg/dL, rose 72 mg/dL to its peak 55 minutes later.";
  const eventsSpec = {
    version: 1,
    panels: [
      { id: "bands", dataset: { name: "event_response", cohorts: [{ anchor: "Meal" }, { anchor: "CorrectionBolus" }] }, primitive: "range_bands",
        bindings: { x: "minute", y: ["glucoseP50"], band: ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"], series: "series" },
        range: { hours: 720 } },
      { id: "events", dataset: { name: "event_response" }, primitive: "table", bindings: { columns: ["at", "summary"] }, range: { hours: 720 } },
    ],
  };
  const eventResult = {
    spec: eventsSpec,
    units: "mg/dl",
    summary: "events",
    panels: [
      {
        id: "bands", dataset: "event_response", primitive: "range_bands", bindings: eventsSpec.panels[0].bindings, status: "ok", window,
        fields: bandFields, rows: [...slotRows("Meal: all", 1, 0), ...slotRows("Correction bolus: all", 2, 40)],
        cohorts: [{ number: 1, label: "Meal: all" }, { number: 2, label: "Correction bolus: all" }],
        summary: "Cohort 1 (Meal: all): 12 events.", link: "https://dev.nocturne.localhost/reports/loop-analysis/meals-and-events?anchor1=Meal",
      },
      {
        id: "events", dataset: "event_response", primitive: "table", bindings: { columns: ["at", "summary"] }, status: "ok", window,
        fields: [field("at", "Time", "time", null), field("summary", "Summary", "category", null)],
        rows: [{ at: from, summary: sentence }], summary: "1 event.",
      },
    ],
  };

  it("draws range_bands with one set of bands per cohort", async () => {
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US" });

    const chart = page.getByTestId("report-range-bands");
    await expect.element(chart).toBeVisible();
    const series = chart.element().querySelectorAll("[data-testid=report-range-bands-series]");
    expect([...series].map((s) => s.getAttribute("data-series"))).toEqual(["Meal: all", "Correction bolus: all"]);
    await expect.poll(() => series[1].querySelectorAll("path").length).toBeGreaterThanOrEqual(3);
    await expect.element(page.getByText("Correction bolus: all")).toBeVisible();
  });

  it("keeps a cohort's colour when another is off, and draws a comparison as its dashed overlay", async () => {
    const comparison = slotRows("Correction bolus: all (comparison)", 2, 20).map((r) => ({ ...r, range: "comparison" }));
    const onlySecond = {
      ...eventResult,
      panels: [{ ...eventResult.panels[0], rows: [...slotRows("Correction bolus: all", 2, 40), ...comparison] }],
    };
    render(ReportView, { result: { structuredContent: onlySecond }, locale: "en-US" });

    const chart = page.getByTestId("report-range-bands");
    await expect.element(chart).toBeVisible();
    const series = [...chart.element().querySelectorAll("[data-testid=report-range-bands-series]")];
    expect(series.map((s) => s.getAttribute("data-series"))).toEqual(["Correction bolus: all"]);
    expect(series[0].getAttribute("data-color")).toBe(cohortBands(1).median);
    await expect.poll(() => series[0].querySelectorAll("path[stroke-dasharray]").length).toBeGreaterThanOrEqual(1);
    await expect.element(page.getByText("Comparison range")).toBeVisible();
  });

  it("labels each cohort checkbox with the API's label, which names its filter", async () => {
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US", refine: vi.fn(async () => {}) });

    await expect.element(page.getByRole("checkbox", { name: "Meal: all" })).toBeVisible();
    await expect.element(page.getByRole("checkbox", { name: "Correction bolus: all" })).toBeVisible();
  });

  it("shows each cohort's median and middle spread at the hovered slot", async () => {
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US" });

    const chart = page.getByTestId("report-range-bands");
    await expect.element(chart).toBeVisible();
    const box = chart.element().querySelector("svg")!.getBoundingClientRect();
    await chart.hover({ position: { x: box.width * 0.6, y: box.height / 2 } });

    const tooltip = page.getByTestId("report-range-bands-tooltip");
    await expect.element(tooltip).toBeVisible();
    await expect.element(tooltip).toHaveTextContent(/Meal: all\s*1\d\d mg\/dL/);
    await expect.element(tooltip).toHaveTextContent(/Correction bolus: all/);
    await expect.element(tooltip).toHaveTextContent(/P25–P75\s*\d+–\d+ mg\/dL/);
    await expect.element(tooltip).toHaveTextContent(/P10–P90/);
  });

  it("hands the API's sentences for a picked slot to the host", async () => {
    const selectEvent = vi.fn();
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US", selectEvent });

    const chart = page.getByTestId("report-range-bands");
    await expect.element(chart).toBeVisible();
    const box = chart.element().querySelector("svg")!.getBoundingClientRect();
    await chart.click({ position: { x: box.width * 0.6, y: box.height / 2 } });

    await expect.poll(() => selectEvent.mock.calls.length).toBe(1);
    const text = selectEvent.mock.calls[0][0] as string;
    expect(text).toMatch(/^Meal: all, -?\d+ minutes from the event: median \d+ mg\/dL\. Correction bolus: all, /);
  });

  it("gives one glucose series the glucose chart's treatment: target shaded, limits drawn, median coloured by range", async () => {
    const hours = Array.from({ length: 24 }, (_, h) => h * 60);
    const profile = {
      spec: { version: 1 },
      units: "mg/dl",
      summary: "profile",
      panels: [
        {
          id: "profile", dataset: "glucose_distribution_by_hour", primitive: "range_bands", status: "ok", window,
          bindings: { x: "hour", y: ["glucoseP50"], band: ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"] },
          fields: [
            field("hour", "Hour", "time_of_day", "min"),
            field("glucoseP50", "Glucose median", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true }),
            field("glucoseP10", "Glucose P10", "number", "mg/dL"),
            field("glucoseP25", "Glucose P25", "number", "mg/dL"),
            field("glucoseP75", "Glucose P75", "number", "mg/dL"),
            field("glucoseP90", "Glucose P90", "number", "mg/dL"),
          ],
          rows: hours.map((hour) => ({ hour, glucoseP10: 80, glucoseP25: 100, glucoseP50: 140, glucoseP75: 190, glucoseP90: 240 })),
          thresholds: { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 },
          summary: "Glucose by hour.",
        },
      ],
    };
    render(ReportView, { result: { structuredContent: profile }, locale: "en-US" });

    const chart = page.getByTestId("report-range-bands");
    await expect.element(chart).toBeVisible();
    await expect.element(page.getByTestId("report-range-bands-target")).toBeInTheDocument();
    const series = chart.element().querySelector("[data-testid=report-range-bands-series]")!;
    await expect.poll(() => [...series.querySelectorAll("path")].some((p) => (p.getAttribute("stroke") ?? "").startsWith("url("))).toBe(true);
  });

  it("offers no host control when the host granted no call", async () => {
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US" });

    await expect.element(page.getByTestId("report-range-bands")).toBeVisible();
    expect(page.getByRole("button", { name: "Load earlier data" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Open in Nocturne" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Open the matching report" }).elements()).toHaveLength(0);
    expect(page.getByRole("checkbox").elements()).toHaveLength(0);
    expect(page.getByText(sentence).element().closest("tr")?.getAttribute("tabindex")).toBeNull();
  });

  it("refines with the normalised spec: load earlier data, and switch a cohort off", async () => {
    const refine = vi.fn(async () => {});
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US", refine });

    await page.getByRole("button", { name: "Load earlier data" }).first().click();
    expect(refine).toHaveBeenLastCalledWith({
      panels: [{ id: "bands", range: { from: new Date(window.from - (window.to - window.from)).toISOString(), to: new Date(window.to).toISOString() } }],
    });

    await page.getByRole("checkbox", { name: "Correction bolus: all" }).click();
    expect(refine).toHaveBeenLastCalledWith({ panels: [{ id: "bands", dataset: { cohorts: [{ anchor: "Meal" }] } }] });
  });

  it("hands the selected event's own sentence to the host, and opens the matching web report", async () => {
    const selectEvent = vi.fn();
    const openLink = vi.fn();
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US", selectEvent, openLink });

    await page.getByText(sentence).click();
    expect(selectEvent).toHaveBeenCalledWith(sentence);

    await page.getByRole("button", { name: "Open the matching report" }).first().click();
    expect(openLink).toHaveBeenCalledWith(eventResult.panels[0].link);
    expect(page.getByRole("button", { name: "Open in Nocturne" }).elements()).toHaveLength(0);
  });

  it("opens the whole spec in the web app with the link the API built", async () => {
    const openLink = vi.fn();
    const openUrl = "https://dev.nocturne.localhost/reports/custom/new?spec=q1Yq";
    render(ReportView, { result: { structuredContent: { ...eventResult, openUrl } }, locale: "en-US", openLink });

    await page.getByRole("button", { name: "Open in Nocturne" }).click();
    expect(openLink).toHaveBeenCalledWith(openUrl);
  });

  it("writes a title as text, never as markup", async () => {
    render(ReportView, {
      result: { structuredContent: { ...twoPanels, spec: { version: 1, title: "<img src=x onerror=alert(1)>" } } },
      locale: "en-US",
    });

    await expect.element(page.getByRole("heading", { name: "<img src=x onerror=alert(1)>" })).toBeVisible();
    expect(document.querySelector("main img")).toBeNull();
  });

  it("writes an event's time in the tenant's zone, as its sentence does", async () => {
    render(ReportView, { result: { structuredContent: { ...eventResult, timeZone: "Australia/Sydney" } }, locale: "en-GB" });

    // 06:00 UTC on 29 October 2026 is 17:00 in Sydney (UTC+11).
    await expect.element(page.getByRole("cell", { name: "29 Oct 2026, 17:00" })).toBeVisible();
  });

  it("writes times in UTC when the tenant has no zone", async () => {
    render(ReportView, { result: { structuredContent: { ...eventResult, timeZone: null } }, locale: "en-GB" });

    await expect.element(page.getByRole("cell", { name: "29 Oct 2026, 06:00" })).toBeVisible();
  });

  it("shows why a refinement failed", async () => {
    render(ReportView, { result: { structuredContent: eventResult }, locale: "en-US", notice: "- panels[0].range: too long" });

    await expect.element(page.getByRole("alert")).toHaveTextContent("too long");
  });
});
