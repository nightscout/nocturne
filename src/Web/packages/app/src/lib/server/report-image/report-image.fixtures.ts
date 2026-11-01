/** Report results of the shape the API returns, a chart of every mark and axis, for the image renderer's tests. */

const HOUR = 3_600_000;
const MINUTE = 60_000;
const to = Date.UTC(2026, 9, 30, 12, 0);
const from = to - 24 * HOUR;
const window = { from, to };
const thresholds = { veryLow: 54, low: 70, high: 180, veryHigh: 250, mgdlPerUnit: 1 };

const field = (name: string, label: string, kind: string, unit: string | null, extra: Record<string, unknown> = {}) => ({
  name, label, kind, unit, ...extra,
});

const report = <P extends { id: string; kind: string }>(panels: P[], title = "Report") => ({
  spec: { version: 1, title },
  units: "mg/dl",
  timeZone: "Etc/UTC",
  glucoseColorMode: "continuous",
  summary: "joined",
  warnings: [],
  panels,
});

const glucoseAt = (i: number) => 140 + 75 * Math.sin(i / 14) + 15 * Math.sin(i / 3);

type Row = Record<string, unknown>;

/** A chart layer as the API sends it. */
const layer = (id: string, title: string, mark: string, side: string, unit: string | null, bindings: Record<string, unknown>, fields: unknown[], rows: Row[], extra: Record<string, unknown> = {}) => ({
  id, title, mark, side, unit, bindings, fields, rows, status: "ok", window, xKind: "time", facts: [], sentence: null, summary: title, ...extra,
});

/** A chart panel as the API sends it. */
const chart = (id: string, title: string, xKind: string, tracks: { id: string; title?: string; height: number; layers: ReturnType<typeof layer>[] }[], extra: Record<string, unknown> = {}) => ({
  id, title, kind: "chart", xKind, status: "ok", window, fields: [], rows: [], tracks, thresholds, summary: title, ...extra,
});

const time = field("time", "Time", "time", null);
const readings = Array.from({ length: 288 }, (_, i) => ({ time: from + i * 5 * MINUTE, glucose: glucoseAt(i), targetLow: 70, targetHigh: 180, iob: 1 + Math.sin(i / 20), cob: 20 + 20 * Math.sin(i / 30) }));

/** The glucose template's four tracks: basal, glucose with the target range and markers, IOB and COB on two sides, and a lane of spans. */
export const glucoseChartPanel = chart("glucose", "Glucose with boluses and carbs", "time", [
  {
    id: "basal", title: "Basal", height: 0.5, layers: [
      layer("basal", "Basal", "area", "left", "U/h", { x: "time", y: ["delivered"] },
        [time, field("delivered", "Delivered basal", "number", "U/h", { color: "insulin-basal", step: true })],
        Array.from({ length: 24 }, (_, h) => ({ time: from + h * HOUR, delivered: 0.6 + (h % 4) * 0.2 }))),
    ],
  },
  {
    id: "glucose", title: "Glucose", height: 2, layers: [
      layer("target", "Target range", "band", "left", "mg/dL", { x: "time", edges: ["targetLow", "targetHigh"] },
        [time, field("targetLow", "Range low", "number", "mg/dL", { color: "glucose-in-range" }), field("targetHigh", "Range high", "number", "mg/dL", { color: "glucose-in-range" })],
        readings, { spans: [{ label: "Range low to Range high", low: "targetLow", high: "targetHigh" }] }),
      layer("glucose", "Glucose", "line", "left", "mg/dL", { x: "time", y: ["glucose"] },
        [time, field("glucose", "Glucose", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true })], readings),
      layer("markers", "Boluses and carbs", "markers", "none", null, { x: "time", y: ["insulin", "carbs"] },
        [time, field("insulin", "Bolus", "number", "U", { marker: "bolus" }), field("carbs", "Carbs", "number", "g", { marker: "carbs" })],
        [{ time: from + 6 * HOUR, insulin: 4.5, carbs: 45 }],
        { markers: [
          { time: from + 6 * HOUR, kind: "bolus", amount: 4.5, automatic: false },
          { time: from + 6 * HOUR, kind: "carbs", amount: 45 },
          { time: from + 14 * HOUR, kind: "bolus", amount: 0.3, automatic: true },
        ] }),
    ],
  },
  {
    id: "onboard", title: "On board", height: 0.6, layers: [
      layer("iob", "IOB", "line", "left", "U", { x: "time", y: ["iob"] }, [time, field("iob", "IOB", "number", "U", { color: "insulin-bolus" })], readings),
      layer("cob", "COB", "line", "right", "g", { x: "time", y: ["cob"] }, [time, field("cob", "COB", "number", "g", { color: "carbs" })], readings),
    ],
  },
  {
    id: "states", title: "Modes", height: 0.3, layers: [
      layer("spans", "Modes and overrides", "spans", "none", null, { start: "start", end: "end", category: "kind" },
        [field("kind", "Kind", "category", null), field("label", "Name", "category", null, { colorField: "color" }), field("start", "Start", "time", null), field("end", "End", "time", null)],
        [
          { kind: "Pump mode", label: "Automatic", start: from, end: to, color: "pump-mode-automatic" },
          { kind: "Override", label: "Exercise", start: from + 8 * HOUR, end: from + 10 * HOUR, color: "chart-2" },
        ]),
    ],
  },
], { context: "Last 24 hours, 288 readings." });

export const bandsPanel = chart("profile", "Glucose pattern over the day", "time_of_day", [
  {
    id: "t1", height: 1, layers: [
      layer("bands", "Glucose by hour of day · Glucose median", "band", "left", "mg/dL",
        { x: "hour", y: ["glucoseP50"], edges: ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"] },
        [
          field("hour", "Hour", "time_of_day", "min"),
          field("glucoseP50", "Glucose median", "number", "mg/dL", { color: "glucose-in-range", glucoseScale: true }),
          field("glucoseP10", "Glucose P10", "number", "mg/dL"),
          field("glucoseP25", "Glucose P25", "number", "mg/dL"),
          field("glucoseP75", "Glucose P75", "number", "mg/dL"),
          field("glucoseP90", "Glucose P90", "number", "mg/dL"),
        ],
        Array.from({ length: 24 }, (_, h) => {
          const m = 135 + 35 * Math.sin((h / 24) * 2 * Math.PI);
          return { hour: h * 60, glucoseP50: m, glucoseP10: m - 50, glucoseP25: m - 22, glucoseP75: m + 28, glucoseP90: m + 65 };
        }),
        { xKind: "time_of_day", spans: [
          { label: "Glucose P25–P75", low: "glucoseP25", high: "glucoseP75" },
          { label: "Glucose P10–P90", low: "glucoseP10", high: "glucoseP90" },
        ] }),
    ],
  },
], { context: "Last 14 days, 4025 readings." });

/** A scatter of glucose against IOB: points and the least-squares line. */
export const scatterPanel = chart("scatter", "Glucose against insulin on board", "value", [
  {
    id: "t1", height: 1, layers: [
      layer("scatter", "Scatter · IOB and Glucose", "points", "left", "mg/dL", { x: "a", y: ["b"], fit: "fit" },
        [field("a", "IOB", "value", "U"), field("b", "Glucose", "number", "mg/dL", { color: "glucose-in-range" }), field("fit", "Least-squares line", "number", "mg/dL", { color: "muted-foreground" })],
        Array.from({ length: 60 }, (_, i) => ({ a: i / 20, b: 180 - (i / 20) * 25 + 20 * Math.sin(i), fit: 180 - (i / 20) * 25 })),
        { xKind: "value", facts: [{ label: "r", value: "-0.71" }] }),
    ],
  },
], { grid: 15 });

/** A lag correlation: r at each lag. */
export const lagPanel = chart("lag", "Correlation with glucose shifted later", "lag", [
  {
    id: "t1", height: 1, layers: [
      layer("lag", "Lag correlation · IOB and Glucose", "line", "left", null, { x: "lag", y: ["r"] },
        [field("lag", "Lag", "lag", "min"), field("r", "Correlation (r)", "number", null, { color: "primary" })],
        Array.from({ length: 25 }, (_, i) => ({ lag: -180 + i * 15, r: -0.6 * Math.exp(-(((i - 15) / 5) ** 2)) })),
        { xKind: "lag" }),
    ],
  },
], { grid: 15 });

/** Bars over days, stacked. */
export const dayBarsPanel = chart("week", "Time in range by day", "day", [
  {
    id: "t1", height: 1, layers: [
      layer("days", "Daily summary · Below range, In range, Above range", "bars", "left", "%", { x: "day", y: ["timeBelow", "timeInRange", "timeAbove"], stacked: true, orientation: "vertical" },
        [
          field("day", "Day", "day", null),
          field("timeBelow", "Below range", "percent", "%", { color: "glucose-low" }),
          field("timeInRange", "In range", "percent", "%", { color: "glucose-in-range" }),
          field("timeAbove", "Above range", "percent", "%", { color: "glucose-high" }),
        ],
        Array.from({ length: 7 }, (_, d) => ({ day: from - (6 - d) * 24 * HOUR, timeBelow: 2 + (d % 3), timeInRange: 60 + d * 2, timeAbove: 38 - d * 2 - (d % 3) })),
        { xKind: "day" }),
    ],
  },
]);

const rangeFields = [
  field("veryLow", "Very low", "percent", "%", { color: "glucose-very-low" }),
  field("low", "Low", "percent", "%", { color: "glucose-low" }),
  field("inRange", "In range", "percent", "%", { color: "glucose-in-range" }),
  field("high", "High", "percent", "%", { color: "glucose-high" }),
  field("veryHigh", "Very high", "percent", "%", { color: "glucose-very-high" }),
];

export const stackedBarPanel = chart("ranges", "Time in each glucose range", "category", [
  {
    id: "t1", height: 1, layers: [
      layer("ranges", "Time in each glucose range", "bars", "left", "%",
        { x: "period", y: ["veryLow", "low", "inRange", "high", "veryHigh"], stacked: true, orientation: "horizontal" },
        [field("period", "Period", "category", null), ...rangeFields],
        [{ period: "2026-10-16 to 2026-10-30", veryLow: 0.4, low: 1.6, inRange: 71.8, high: 20.9, veryHigh: 5.3 }],
        { xKind: "category" }),
    ],
  },
]);

export const barPanel = chart("days", "Last week by day", "category", [
  {
    id: "t1", height: 1, layers: [
      layer("days", "Daily summary", "bars", "left", "%", { x: "date", y: ["timeBelow", "timeInRange", "timeAbove"], stacked: true, orientation: "vertical" },
        [
          field("date", "Date", "category", null),
          field("timeBelow", "Below range", "percent", "%", { color: "glucose-low" }),
          field("timeInRange", "In range", "percent", "%", { color: "glucose-in-range" }),
          field("timeAbove", "Above range", "percent", "%", { color: "glucose-high" }),
        ],
        Array.from({ length: 7 }, (_, d) => ({
          date: `2026-10-${String(24 + d).padStart(2, "0")}`,
          timeBelow: 2 + (d % 3),
          timeInRange: 60 + d * 2,
          timeAbove: 38 - d * 2 - (d % 3),
        })),
        { xKind: "category" }),
    ],
  },
]);

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
  kind: "comparison",
  tracks: [],
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

export const statTilesPanel = {
  id: "figures",
  title: "Key figures",
  dataset: "glucose_statistics",
  kind: "stat_tiles",
  tracks: [],
  bindings: { columns: ["mean"] },
  status: "ok",
  window,
  fields: [field("mean", "Mean glucose", "number", "mg/dL")],
  rows: [{ mean: 152 }],
  summary: "Glucose statistics.",
};

/** A report holding every panel above: every mark, every axis and every kind of panel. */
export const everyPanel = report(
  [glucoseChartPanel, bandsPanel, stackedBarPanel, barPanel, comparisonPanel, scatterPanel, lagPanel, dayBarsPanel, statTilesPanel].map((p, i) => ({
    ...p,
    id: `${p.id}-${i}`,
  })),
  "Every panel",
);
