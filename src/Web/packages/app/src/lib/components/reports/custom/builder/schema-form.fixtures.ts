import type {
  CustomReportCatalogue,
  CustomReportDataset,
} from "#lib/api/generated/nocturne-api-client.js";
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

function dataset(overrides: Partial<CustomReportDataset>): CustomReportDataset {
  return {
    name: "glucose_statistics",
    label: "Glucose statistics",
    description: "Time in range, mean, GMI, CV and the other glucose figures of the range.",
    requiredScope: "glucose.read",
    permitted: true,
    parameters: { type: "object", additionalProperties: false },
    fields: [
      { name: "timeInRangePercent", label: "Time in range", kind: "percent", unit: "%", description: "x", permitted: true },
      { name: "mean", label: "Mean", kind: "number", unit: "mg/dL", description: "x", permitted: true },
      { name: "tdd", label: "Insulin per day", kind: "number", unit: "U", description: "x", scope: "treatments.read", permitted: false },
    ],
    primitives: [
      {
        name: "stat_tiles",
        label: "Figures",
        maxRangeHours: 2160,
        rangeLimit: "This panel covers at most 90 days.",
        defaultBindings: { columns: ["timeInRangePercent", "mean", "tdd"] },
        bindings: [{ key: "columns", label: "Columns", input: "fields", fields: ["timeInRangePercent", "mean", "tdd"], counts: [], required: false, choices: [] }],
      },
      {
        name: "table",
        label: "Table",
        maxRangeHours: 2160,
        rangeLimit: "This panel covers at most 90 days.",
        defaultBindings: { columns: ["timeInRangePercent", "mean", "tdd"] },
        bindings: [{ key: "columns", label: "Columns", input: "fields", fields: ["timeInRangePercent", "mean", "tdd"], counts: [], required: false, choices: [] }],
      },
    ],
    defaultRangeHours: 336,
    maxPanelsPerReport: 6,
    ...overrides,
  };
}

/** A catalogue of three datasets: statistics, one with a parameter of every shape, and one the caller cannot read. */
export const catalogue: CustomReportCatalogue = {
  maxPanels: 6,
  maxTitleLength: 200,
  maxRangeHours: 2160,
  maxNameLength: 120,
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
    }),
  ],
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
        dataset: { name: "glucose_statistics" },
        primitive: "stat_tiles",
        bindings: { columns: ["timeInRangePercent", "mean", "tdd"] },
        range: { hours: 336 },
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
