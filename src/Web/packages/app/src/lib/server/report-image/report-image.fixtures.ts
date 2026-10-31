/** Report results of the shape the API returns, one per chart primitive, for the image renderer's tests. */

const HOUR = 3_600_000;
const MINUTE = 60_000;
const to = Date.UTC(2026, 9, 30, 12, 0);
const from = to - 24 * HOUR;
const window = { from, to };
const thresholds = { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 };

const field = (name: string, label: string, kind: string, unit: string | null, extra: Record<string, unknown> = {}) => ({
  name, label, kind, unit, ...extra,
});

const report = <P extends { id: string; primitive: string }>(panels: P[], title = "Report") => ({
  spec: { version: 1, title },
  units: "mg/dl",
  timeZone: "Etc/UTC",
  glucoseColorMode: "continuous",
  summary: "joined",
  warnings: [],
  panels,
});

const glucoseAt = (i: number) => 140 + 75 * Math.sin(i / 14) + 15 * Math.sin(i / 3);

export const timeSeriesPanel = {
  id: "glucose",
  title: "Glucose with boluses and carbs",
  dataset: "glucose_series",
  primitive: "time_series",
  bindings: { x: "time", y: ["glucose"], band: ["targetLow", "targetHigh"] },
  status: "ok",
  window,
  context: "Last 24 hours, 288 readings.",
  fields: [
    field("time", "Time", "time", null),
    field("glucose", "Glucose", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true }),
    field("targetLow", "Range low", "number", "mg/dL"),
    field("targetHigh", "Range high", "number", "mg/dL"),
  ],
  rows: Array.from({ length: 288 }, (_, i) => ({ time: from + i * 5 * MINUTE, glucose: glucoseAt(i), targetLow: 70, targetHigh: 180 })),
  thresholds,
  markers: [
    { time: from + 6 * HOUR, kind: "bolus", amount: 4.5, automatic: false },
    { time: from + 6 * HOUR, kind: "carbs", amount: 45 },
    { time: from + 14 * HOUR, kind: "bolus", amount: 0.3, automatic: true },
  ],
  summary: "Glucose from ...",
};

export const rangeBandsPanel = {
  id: "profile",
  title: "Glucose pattern over the day",
  dataset: "glucose_distribution_by_hour",
  primitive: "range_bands",
  bindings: { x: "hour", y: ["glucoseP50"], band: ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"] },
  status: "ok",
  window,
  context: "Last 14 days, 4025 readings.",
  fields: [
    field("hour", "Hour", "time_of_day", "min"),
    field("glucoseP50", "Glucose median", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true }),
    field("glucoseP10", "Glucose P10", "number", "mg/dL"),
    field("glucoseP25", "Glucose P25", "number", "mg/dL"),
    field("glucoseP75", "Glucose P75", "number", "mg/dL"),
    field("glucoseP90", "Glucose P90", "number", "mg/dL"),
  ],
  rows: Array.from({ length: 24 }, (_, h) => {
    const m = 135 + 35 * Math.sin((h / 24) * 2 * Math.PI);
    return { hour: h * 60, glucoseP50: m, glucoseP10: m - 50, glucoseP25: m - 22, glucoseP75: m + 28, glucoseP90: m + 65 };
  }),
  thresholds,
  spans: [
    { label: "Glucose P25–P75", low: "glucoseP25", high: "glucoseP75" },
    { label: "Glucose P10–P90", low: "glucoseP10", high: "glucoseP90" },
  ],
  summary: "Glucose by hour of day.",
};

const rangeFields = [
  field("veryLow", "Very low", "percent", "%", { color: "glucose-very-low" }),
  field("low", "Low", "percent", "%", { color: "glucose-low" }),
  field("inRange", "In range", "percent", "%", { color: "glucose-in-range" }),
  field("high", "High", "percent", "%", { color: "glucose-high" }),
  field("veryHigh", "Very high", "percent", "%", { color: "glucose-very-high" }),
];

export const stackedBarPanel = {
  id: "ranges",
  title: "Time in each glucose range",
  dataset: "glucose_distribution",
  primitive: "bar",
  status: "ok",
  window,
  bindings: { x: "period", y: ["veryLow", "low", "inRange", "high", "veryHigh"], stacked: true, orientation: "horizontal" },
  fields: [field("period", "Period", "category", null), ...rangeFields],
  rows: [{ period: "2026-10-16 to 2026-10-30", veryLow: 0.4, low: 1.6, inRange: 71.8, high: 20.9, veryHigh: 5.3 }],
  summary: "Glucose distribution.",
};

export const barPanel = {
  id: "days",
  title: "Last week by day",
  dataset: "daily_summary",
  primitive: "bar",
  status: "ok",
  window,
  bindings: { x: "date", y: ["timeBelow", "timeInRange", "timeAbove"], stacked: true },
  fields: [
    field("date", "Date", "category", null),
    field("timeBelow", "Below range", "percent", "%", { color: "glucose-low" }),
    field("timeInRange", "In range", "percent", "%", { color: "glucose-in-range" }),
    field("timeAbove", "Above range", "percent", "%", { color: "glucose-high" }),
  ],
  rows: Array.from({ length: 7 }, (_, d) => ({
    date: `2026-10-${String(24 + d).padStart(2, "0")}`,
    timeBelow: 2 + (d % 3),
    timeInRange: 60 + d * 2,
    timeAbove: 38 - d * 2 - (d % 3),
  })),
  summary: "Daily summary.",
};

const comparisonRow = (metric: string, group: string, label: string, a: number | null, b: number | null, unit: string, verdict: string | null, color: string | null = null) => ({
  metric, group, label, a, b, unit, verdict, color,
  delta: a !== null && b !== null ? Math.round((b - a) * 10) / 10 : null,
  changePercent: a !== null && b !== null && a !== 0 ? Math.round(((b - a) / a) * 1000) / 10 : null,
  direction: a === null || b === null ? null : b > a ? "up" : b < a ? "down" : "flat",
});

export const comparisonPanel = {
  id: "comparison",
  title: "Two periods side by side",
  dataset: "period_comparison",
  primitive: "comparison",
  status: "ok",
  window,
  bindings: { columns: ["metric", "group", "label", "a", "b", "delta", "changePercent", "unit", "verdict", "color"] },
  fields: [
    field("metric", "Figure key", "category", null), field("group", "Group", "category", null), field("label", "Figure", "category", null),
    field("a", "Prior 14 days", "number", null), field("b", "Last 14 days", "number", null),
    field("delta", "Change", "number", null), field("changePercent", "Change (%)", "number", "%"), field("unit", "Unit", "category", null),
    field("verdict", "Verdict", "category", null), field("color", "Color", "category", null),
  ],
  rows: [
    comparisonRow("veryLow", "Time in each range", "Very low", 1, 0.5, "%", "better", "glucose-very-low"),
    comparisonRow("low", "Time in each range", "Low", 3, 2.5, "%", "better", "glucose-low"),
    comparisonRow("inRange", "Time in each range", "In range", 60, 70, "%", "better", "glucose-in-range"),
    comparisonRow("high", "Time in each range", "High", 30, 22, "%", "better", "glucose-high"),
    comparisonRow("veryHigh", "Time in each range", "Very high", 6, 5, "%", "better", "glucose-very-high"),
    comparisonRow("gmi", "Overview", "GMI", 7.1, 6.9, "%", "better"),
    comparisonRow("cv", "Overview", "Variability (CV)", 34, 38, "%", "worse"),
    comparisonRow("mean", "Overview", "Mean glucose", 160, 150, "mg/dL", null),
    comparisonRow("insulinPerDay", "Insulin and carbs", "Total insulin per day", 38, 40, "U", null),
  ],
  summary: "Period comparison.",
};

export const glucoseChartPanel = {
  id: "glucose",
  title: null,
  dataset: "glucose_series",
  primitive: "glucose_chart",
  status: "ok",
  bindings: {},
  window: { from: to - 6 * HOUR, to },
  context: "Last 6 hours, 72 readings.",
  fields: [],
  rows: [],
  summary: "Glucose from ...",
  chart: {
    startTime: to - 6 * HOUR,
    endTime: to,
    thresholds: { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300, targetLow: 90, targetHigh: 140 },
    glucoseData: Array.from({ length: 72 }, (_, i) => ({ time: to - 6 * HOUR + i * 5 * MINUTE, sgv: Math.round(glucoseAt(i * 2) ) })),
    bolusMarkers: [{ time: to - 4 * HOUR, insulin: 4.5, bolusType: "Bolus" }],
    carbMarkers: [{ time: to - 4 * HOUR, carbs: 45 }],
    basalSeries: Array.from({ length: 13 }, (_, i) => ({ timestamp: to - 6 * HOUR + i * 30 * MINUTE, rate: 0.6 + (i % 4) * 0.2, scheduledRate: 0.8 })),
    iobSeries: [], cobSeries: [], deviceEventMarkers: [], systemEventMarkers: [], trackerMarkers: [], basalInjectionMarkers: [],
    bgCheckMarkers: [], pumpModeSpans: [], profileSpans: [], overrideSpans: [], activitySpans: [], tempBasalSpans: [],
    basalDeliverySpans: [], heartRateSeries: [], stepSeries: [], maxBasalRate: 1.4, maxIob: 0, maxCob: 0,
  },
};

export const statTilesPanel = {
  id: "figures",
  title: "Key figures",
  dataset: "glucose_statistics",
  primitive: "stat_tiles",
  bindings: { columns: ["mean"] },
  status: "ok",
  window,
  fields: [field("mean", "Mean glucose", "number", "mg/dL")],
  rows: [{ mean: 152 }],
  summary: "Glucose statistics.",
};

/** A report holding every panel above. */
export const everyPrimitive = report(
  [timeSeriesPanel, rangeBandsPanel, stackedBarPanel, barPanel, comparisonPanel, glucoseChartPanel, statTilesPanel].map((p, i) => ({
    ...p,
    id: `${p.id}-${i}`,
  })),
  "Every primitive",
);
