import type {
  CustomReportBindings,
  CustomReportCatalogue,
  CustomReportDataset,
  CustomReportDatasetPrimitive,
} from "#lib/api/generated/nocturne-api-client.js";

/** A range as a spec writes it: the last N hours, or from and to (ISO 8601 with an offset). */
export type SpecRange = { hours: number } | { from: string; to: string };

export type DatasetParameters = Record<string, unknown>;

/** One panel being edited. `key` is the card's identity across reorders; it is not sent. */
export interface DraftPanel {
  key: number;
  id: string;
  title?: string;
  dataset: string;
  parameters: DatasetParameters;
  primitive: string;
  bindings: CustomReportBindings;
  range: SpecRange | null;
}

let nextKey = 0;

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readRange(value: unknown): SpecRange | null {
  if (!isObject(value)) return null;
  if (typeof value.hours === "number") return { hours: value.hours };
  if (typeof value.from === "string" && typeof value.to === "string") return { from: value.from, to: value.to };
  return null;
}

function strings(value: unknown): string[] | undefined {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : undefined;
}

function readBindings(value: unknown): CustomReportBindings {
  if (!isObject(value)) return {};
  return {
    x: typeof value.x === "string" ? value.x : undefined,
    y: strings(value.y),
    band: strings(value.band),
    series: typeof value.series === "string" ? value.series : undefined,
    columns: strings(value.columns),
    stacked: typeof value.stacked === "boolean" ? value.stacked : undefined,
    orientation: typeof value.orientation === "string" ? value.orientation : undefined,
  };
}

/** Whether two ranges are the same; a normalised spec writes the report's range into each panel that takes it. */
function sameRange(a: SpecRange | null, b: SpecRange | null): boolean {
  return a !== null && b !== null && JSON.stringify(a) === JSON.stringify(b);
}

/** The bindings as a spec writes them: only the keys that have a value. */
function bindingsJson(bindings: CustomReportBindings): Record<string, unknown> {
  return Object.fromEntries(Object.entries(bindings).filter(([, v]) => v !== undefined && v !== null));
}

/** A copy without the keys whose value is undefined, so the spec sends only what was chosen. */
function withoutUndefined(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(withoutUndefined);
  if (!isObject(value)) return value;
  return compact(value);
}

function compact(value: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(value)
      .filter(([, v]) => v !== undefined)
      .map(([k, v]) => [k, withoutUndefined(v)]),
  );
}

/**
 * A report spec as the builder edits it. It holds what the member chose and copies defaults from the
 * catalogue; the API's validator decides what is valid and fills everything else.
 */
export class SpecDraft {
  title = $state("");
  range = $state<SpecRange | null>(null);
  panels = $state<DraftPanel[]>([]);

  readonly #catalogue: CustomReportCatalogue;

  constructor(catalogue: CustomReportCatalogue, spec?: unknown) {
    this.#catalogue = catalogue;
    if (isObject(spec)) this.#load(spec);
  }

  get maxPanels(): number {
    return this.#catalogue.maxPanels;
  }

  get maxTitleLength(): number {
    return this.#catalogue.maxTitleLength;
  }

  get maxRangeHours(): number {
    return this.#catalogue.maxRangeHours;
  }

  get maxNameLength(): number {
    return this.#catalogue.maxNameLength;
  }

  get datasets(): CustomReportDataset[] {
    return this.#catalogue.datasets;
  }

  get atPanelLimit(): boolean {
    return this.panels.length >= this.maxPanels;
  }

  dataset(name: string): CustomReportDataset | undefined {
    return this.#catalogue.datasets.find((d) => d.name === name);
  }

  primitive(panel: DraftPanel): CustomReportDatasetPrimitive | undefined {
    return this.dataset(panel.dataset)?.primitives?.find((p) => p.name === panel.primitive);
  }

  #load(spec: Record<string, unknown>) {
    this.title = typeof spec.title === "string" ? spec.title : "";
    this.range = readRange(spec.range);
    const panels = Array.isArray(spec.panels) ? spec.panels : [];
    this.panels = panels.filter(isObject).map((p, i) => {
      const dataset = isObject(p.dataset) ? p.dataset : {};
      const { name, ...parameters } = dataset;
      return {
        key: nextKey++,
        id: typeof p.id === "string" ? p.id : `p${i + 1}`,
        title: typeof p.title === "string" ? p.title : undefined,
        dataset: typeof name === "string" ? name : "",
        parameters,
        primitive: typeof p.primitive === "string" ? p.primitive : "",
        bindings: readBindings(p.bindings),
        range: sameRange(readRange(p.range), this.range) ? null : readRange(p.range),
      };
    });
  }

  /** The spec to run or save. */
  toSpec(): Record<string, unknown> {
    return compact({
      version: 1,
      title: this.title.trim() === "" ? undefined : this.title.trim(),
      range: this.range ?? undefined,
      panels: this.panels.map((p) => ({
        id: p.id,
        title: p.title?.trim() ? p.title.trim() : undefined,
        dataset: { name: p.dataset, ...p.parameters },
        primitive: p.primitive,
        bindings: bindingsJson(p.bindings),
        range: p.range ?? undefined,
      })),
    });
  }

  #freeId(): string {
    const taken = this.panels.map((p) => p.id);
    for (let n = this.panels.length + 1; ; n++) if (!taken.includes(`p${n}`)) return `p${n}`;
  }

  /** A panel of `datasetName`, or of the first dataset the caller may read, drawn its first way. */
  addPanel(datasetName?: string) {
    if (this.atPanelLimit) return;
    const datasets = this.#catalogue.datasets;
    const dataset = datasets.find((d) => d.name === datasetName) ?? datasets.find((d) => d.permitted) ?? datasets[0];
    if (!dataset?.name) return;
    const primitive = dataset.primitives?.[0];
    this.panels.push({
      key: nextKey++,
      id: this.#freeId(),
      title: dataset.label,
      dataset: dataset.name,
      parameters: {},
      primitive: primitive?.name ?? "",
      bindings: { ...(primitive?.defaultBindings ?? {}) },
      range: null,
    });
  }

  removePanel(index: number) {
    this.panels.splice(index, 1);
  }

  movePanel(index: number, by: -1 | 1) {
    const to = index + by;
    if (to < 0 || to >= this.panels.length) return;
    const [panel] = this.panels.splice(index, 1);
    this.panels.splice(to, 0, panel);
  }

  /**
   * Another dataset: its parameters start empty, the panel keeps its primitive where the dataset
   * allows it, else takes the first, with that primitive's bindings; a title still the old dataset's
   * name becomes the new one's.
   */
  setDataset(index: number, name: string) {
    const panel = this.panels[index];
    const dataset = this.dataset(name);
    if (!panel || !dataset) return;
    const primitive = dataset.primitives?.find((p) => p.name === panel.primitive) ?? dataset.primitives?.[0];
    if (!panel.title?.trim() || panel.title === this.dataset(panel.dataset)?.label) panel.title = dataset.label;
    panel.dataset = name;
    panel.parameters = {};
    panel.primitive = primitive?.name ?? "";
    panel.bindings = { ...(primitive?.defaultBindings ?? {}) };
  }

  /** Another primitive: the bindings are reset to the ones the catalogue gives it. */
  setPrimitive(index: number, name: string) {
    const panel = this.panels[index];
    const primitive = panel ? this.dataset(panel.dataset)?.primitives?.find((p) => p.name === name) : undefined;
    if (!panel || !primitive) return;
    panel.primitive = name;
    panel.bindings = { ...(primitive.defaultBindings ?? {}) };
  }

  setParameters(index: number, parameters: DatasetParameters) {
    const panel = this.panels[index];
    if (panel) panel.parameters = parameters;
  }

  setBinding(index: number, key: keyof CustomReportBindings, value: unknown) {
    const panel = this.panels[index];
    if (!panel) return;
    panel.bindings = readBindings({ ...panel.bindings, [key]: value });
  }

  resetBindings(index: number) {
    const panel = this.panels[index];
    const primitive = panel ? this.primitive(panel) : undefined;
    if (panel && primitive) panel.bindings = { ...(primitive.defaultBindings ?? {}) };
  }

  setPanelRange(index: number, range: SpecRange | null) {
    const panel = this.panels[index];
    if (panel) panel.range = range;
  }

  setPanelTitle(index: number, title: string) {
    const panel = this.panels[index];
    if (panel) panel.title = title;
  }
}
