import type {
  CustomReportCatalogue,
  CustomReportDataset,
} from "#lib/api/generated/nocturne-api-client.js";
import { liveCatalogue } from "../workspace/catalogue.fixtures.js";
import type { ParameterSchema } from "./schema-form.js";

/** One node of every shape the form draws, as the API's catalogue writes them. */
export const everyShape: Record<string, ParameterSchema> = {
  "array<object>": {
    type: "array",
    title: "Event groups",
    maxItems: 3,
    items: {
      type: "object",
      title: "Event group",
      required: ["anchor"],
      properties: {
        anchor: { type: "string", title: "Event", enum: ["Meal", "CorrectionBolus"], "x-enumLabels": ["Meal", "Correction bolus"], default: "Meal" },
      },
    },
  },
  "array<string:enum>": {
    type: "array",
    title: "Weekdays",
    items: { type: "string", enum: ["Mon", "Tue", "Wed"], "x-enumLabels": ["Mon", "Tue", "Wed"] },
  },
  "array<string:uuid>:foods": {
    type: "array",
    title: "Foods",
    "x-input": "foods",
    items: { type: "string", format: "uuid" },
  },
  boolean: { type: "boolean", title: "Exclude illness", description: "Leave out illness time.", default: true },
  integer: { type: "integer", title: "Minutes between rows", minimum: 1, maximum: 60, default: 5 },
  "integer:enum": { type: "integer", title: "Minutes before each event", enum: [0, 30, 60], "x-enumLabels": ["0", "30", "60"], default: 60 },
  "integer:minute-of-day": { type: "integer", title: "From", minimum: 0, maximum: 1439, "x-input": "minute-of-day", default: 0 },
  number: { type: "number", title: "Smallest carb entry (g)", minimum: 0, maximum: 1000 },
  object: {
    type: "object",
    title: "Period A",
    required: ["from", "to"],
    properties: {
      from: { type: "string", format: "date", title: "From" },
      to: { type: "string", format: "date", title: "To" },
    },
  },
  string: { type: "string", title: "Profile name", maxLength: 100 },
  "string:date": { type: "string", format: "date", title: "First day" },
  "string:enum": { type: "string", title: "Setting", enum: ["basal", "isf"], "x-enumLabels": ["Basal", "ISF"] },
  "string:food-category": { type: "string", title: "Food category", "x-input": "food-category", maxLength: 100 },
};

/** A parameters schema holding one parameter of every shape, keyed by a name for each. */
export const everyShapeParameters: ParameterSchema = {
  type: "object",
  properties: Object.fromEntries(
    Object.values(everyShape).map((node, i) => [`p${i}`, node]),
  ),
};

const columnsKinds = [
  { name: "stat_tiles", columns: ["timeInRangePercent", "mean", "tdd"] },
  { name: "table", columns: ["timeInRangePercent", "mean", "tdd"] },
];

function dataset(overrides: Partial<CustomReportDataset>): CustomReportDataset {
  return {
    name: "glucose_statistics",
    label: "Glucose statistics",
    description: "Time in range, mean, GMI, CV and the other glucose figures of the range.",
    requiredScope: "glucose.read",
    permitted: true,
    parameters: { type: "object", additionalProperties: false },
    fields: [
      { name: "timeInRangePercent", label: "Time in range", kind: "percent", unit: "%", description: "x", permitted: true, setting: false },
      { name: "mean", label: "Mean", kind: "number", unit: "mg/dL", description: "x", permitted: true, setting: false },
      { name: "tdd", label: "Insulin per day", kind: "number", unit: "U", description: "x", scope: "treatments.read", permitted: false, setting: false },
    ],
    xKinds: [],
    panelKinds: columnsKinds,
    marks: [],
    defaultRangeHours: 336,
    maxRangeHours: 2160,
    rangeLimit: "This data covers at most 90 days.",
    maxReadsPerReport: 16,
    ...overrides,
  };
}

const time = { name: "time", label: "Time", kind: "time", description: "x", permitted: true, setting: false };
const glucose = { name: "glucose", label: "Glucose", kind: "number", unit: "mg/dL", description: "x", permitted: true, aggregate: "mean", setting: false };
const iob = { name: "iob", label: "IOB", kind: "number", unit: "U", description: "x", permitted: true, aggregate: "mean", scope: "treatments.read", setting: false };

const lineSlots = [
  { key: "x", label: "Horizontal axis", input: "field", fields: ["time"], counts: [], required: true, choices: [] },
  { key: "y", label: "Lines", input: "fields", fields: ["glucose", "iob"], counts: [], required: true, choices: [] },
];

/** A catalogue of four datasets (statistics, one with a parameter of every shape, one the caller cannot read, glucose readings to chart) and two ops. */
export const catalogue: CustomReportCatalogue = {
  maxPanels: 6,
  maxTracks: 4,
  maxLayers: 8,
  maxReads: 16,
  maxTitleLength: 200,
  maxRangeHours: 2160,
  maxNameLength: 120,
  maxGridMinutes: 1440,
  panelKinds: [
    { name: "chart", label: "Chart" },
    { name: "stat_tiles", label: "Statistics tiles" },
    { name: "table", label: "Table" },
    { name: "comparison", label: "Comparison" },
  ],
  marks: [
    { name: "line", label: "Line", hasAxis: true, xKinds: ["time"], slots: [] },
    { name: "points", label: "Points", hasAxis: true, xKinds: ["time", "value"], slots: [] },
  ],
  ops: [
    {
      name: "rolling_mean",
      label: "Rolling mean",
      description: "The mean of the source over the given minutes up to each slot.",
      sources: 1,
      parameters: { type: "object", properties: { minutes: { type: "integer", title: "Window (minutes)", minimum: 15, maximum: 10080, default: 60 } } },
      xRule: "The source's axis.",
      unitRule: "The source's unit.",
      sourceXKinds: ["time"],
      outputXKinds: ["time"],
      sourceFieldKinds: ["number", "percent"],
      marks: [{ name: "line", defaultBindings: { x: "x", y: ["value"] } }],
    },
    {
      name: "scatter",
      label: "Scatter",
      description: "Each slot with both sources as a point.",
      sources: 2,
      parameters: { type: "object", properties: {} },
      xRule: "Values of source a.",
      unitRule: "Source b's unit up, source a's along the axis.",
      sourceXKinds: ["time"],
      outputXKinds: ["value"],
      sourceFieldKinds: ["number", "percent"],
      marks: [{ name: "points", defaultBindings: { x: "a", y: ["b"], fit: "fit" } }],
    },
  ],
  datasets: [
    dataset({}),
    dataset({
      name: "every_shape",
      label: "Every setting",
      description: "A dataset with a setting of every kind.",
      parameters: everyShapeParameters,
    }),
    dataset({
      name: "treatments",
      label: "Treatments",
      description: "Boluses, carbs, temp basals, notes, overrides and long-acting injections.",
      requiredScope: "treatments.read",
      permitted: false,
      panelKinds: [{ name: "stat_tiles", columns: ["timeInRangePercent"] }, { name: "table", columns: ["timeInRangePercent"] }],
    }),
    dataset({
      name: "glucose_series",
      label: "Glucose readings",
      description: "Glucose readings over time.",
      fields: [time, glucose, iob],
      xKinds: ["time"],
      panelKinds: [{ name: "table", columns: ["time", "glucose", "iob"] }],
      marks: [
        { name: "line", defaultBindings: { x: "time", y: ["glucose"] }, bindings: lineSlots },
        { name: "points", defaultBindings: { x: "time", y: ["glucose"] }, bindings: lineSlots },
      ],
      defaultRangeHours: 6,
    }),
  ],
  // The form builder reads none of the workspace's vocabulary; the live catalogue's stands in.
  workspace: liveCatalogue.workspace,
};

/** A template spec as the API normalises it: one stat tiles panel over 14 days. */
export const statisticsTemplate = {
  id: "stats",
  name: "Statistics",
  description: "The key figures of the last 14 days.",
  spec: {
    version: 1,
    title: "Two weeks",
    range: { hours: 336 },
    panels: [
      {
        id: "p1",
        kind: "stat_tiles",
        dataset: { name: "glucose_statistics" },
        bindings: { columns: ["timeInRangePercent", "mean", "tdd"] },
        range: { hours: 336 },
      },
    ],
  },
};

/** A template spec as the API normalises it: a chart of glucose and IOB on two tracks. */
export const chartTemplate = {
  id: "glucose",
  name: "Glucose chart",
  description: "Glucose and IOB.",
  spec: {
    version: 1,
    title: "Glucose",
    range: { hours: 6 },
    panels: [
      {
        id: "glucose",
        kind: "chart",
        range: { hours: 6 },
        grid: null,
        tracks: [
          { id: "glucose", title: null, height: 2, layers: [{ id: "glucose", dataset: { name: "glucose_series" }, mark: "line", bindings: { x: "time", y: ["glucose"], edges: null } }] },
          { id: "onboard", height: 1, layers: [{ id: "iob", dataset: { name: "glucose_series" }, mark: "line", bindings: { x: "time", y: ["iob"] } }] },
        ],
      },
    ],
  },
};

/** A run's result with one panel of `base`'s first panel for each panel the spec sent, under the spec's panel ids. */
export function resultFor<T extends { panels: { id: string }[] }>(spec: Record<string, unknown>, base: T): T {
  const panels = Array.isArray(spec.panels) ? spec.panels : [];
  return {
    ...base,
    panels: panels.map((p: { id?: string }) => ({ ...base.panels[0], id: p.id ?? base.panels[0].id })),
  };
}
