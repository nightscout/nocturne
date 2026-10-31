/**
 * The JSON Schema the API's report catalogue gives each dataset's parameters
 * (Nocturne.API/Reports/ReportParametersSchema.cs), and which control the builder draws for each
 * shape of node in it.
 */
export interface ParameterSchema {
  type?: string;
  title?: string;
  description?: string;
  enum?: (string | number)[];
  "x-enumLabels"?: string[];
  "x-input"?: string;
  format?: string;
  minimum?: number;
  maximum?: number;
  maxLength?: number;
  minItems?: number;
  maxItems?: number;
  items?: ParameterSchema;
  properties?: Record<string, ParameterSchema>;
  required?: string[];
  default?: unknown;
}

/**
 * Every shape of parameter the form draws. The API pins the same list
 * (`ReportCatalogueTests.FormShapes`), and `schema-form.test.ts` holds the two equal, so a new shape
 * in the catalogue fails a test until the form draws it.
 */
export const FORM_SHAPES = [
  "array<object>",
  "array<string:enum>",
  "array<string:uuid>:foods",
  "boolean",
  "integer",
  "integer:enum",
  "integer:minute-of-day",
  "number",
  "object",
  "string",
  "string:date",
  "string:enum",
  "string:food-category",
] as const;

export type FormShape = (typeof FORM_SHAPES)[number];

/** A node as the form tells it apart: its type, then enum, format and input kind. */
export function shapeOf(node: ParameterSchema): string {
  const type = node.type ?? "";
  if (type === "array") return `array<${shapeOf(node.items ?? {})}>` + (node["x-input"] ? `:${node["x-input"]}` : "");
  const parts = [type];
  if (node.enum) parts.push("enum");
  if (node.format === "date" || node.format === "uuid") parts.push(node.format);
  if (node["x-input"]) parts.push(node["x-input"]);
  return parts.join(":");
}

export type ParameterControl =
  | "list"
  | "toggles"
  | "foods"
  | "switch"
  | "number"
  | "select"
  | "time"
  | "group"
  | "text"
  | "date"
  | "food-category";

const CONTROLS: Record<FormShape, ParameterControl> = {
  "array<object>": "list",
  "array<string:enum>": "toggles",
  "array<string:uuid>:foods": "foods",
  boolean: "switch",
  integer: "number",
  "integer:enum": "select",
  "integer:minute-of-day": "time",
  number: "number",
  object: "group",
  string: "text",
  "string:date": "date",
  "string:enum": "select",
  "string:food-category": "food-category",
};

/** The control for a node, or null for a shape the form cannot draw. */
function isFormShape(shape: string): shape is FormShape {
  return FORM_SHAPES.some((s) => s === shape);
}

export function controlFor(node: ParameterSchema): ParameterControl | null {
  const shape = shapeOf(node);
  return isFormShape(shape) ? CONTROLS[shape] : null;
}

/** An enum's values with the API's words for each. */
export function choicesOf(node: ParameterSchema): { value: string | number; label: string }[] {
  const values = node.enum ?? node.items?.enum ?? [];
  const labels = node["x-enumLabels"] ?? node.items?.["x-enumLabels"] ?? [];
  return values.map((value, i) => ({ value, label: labels[i] ?? String(value) }));
}

/** `07:30` for 450 minutes after midnight. */
export function minuteToTime(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

/** 450 for `07:30`; null for text that is not a time. */
export function timeToMinute(time: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(time);
  if (!match) return null;
  const minutes = Number(match[1]) * 60 + Number(match[2]);
  return minutes < 24 * 60 ? minutes : null;
}

/** One message from the validator, with the path inside a panel's dataset parameters (`cohorts[0].dayOfWeek`). */
export interface FieldMessage {
  path: string;
  message: string;
}

/**
 * The messages for a field at `path`: the ones that name it, and for a leaf, the ones that name an item
 * of it (`dayOfWeek[0]` for `dayOfWeek`). A group or a list shows only the messages that name it; its
 * fields show theirs.
 */
export function messagesAt(messages: FieldMessage[], path: string, leaf: boolean): string[] {
  return messages
    .filter((m) => m.path === path || (leaf && m.path.startsWith(`${path}[`)))
    .map((m) => m.message);
}
