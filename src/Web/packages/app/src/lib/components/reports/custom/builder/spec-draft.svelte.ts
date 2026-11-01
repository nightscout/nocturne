import type {
  CustomReportCatalogue,
  CustomReportDataset,
  CustomReportLayerBindings,
  CustomReportOp,
} from "#lib/api/generated/nocturne-api-client.js";

/** A range as a spec writes it: the last N hours, or from and to (ISO 8601 with an offset). */
export type SpecRange = { hours: number } | { from: string; to: string };

export type DatasetParameters = Record<string, unknown>;

export type Bindings = Record<string, unknown>;

/** A derived layer's source: one field of a dataset query or of one derived series. */
export interface DraftSource {
  dataset: string | null;
  parameters: DatasetParameters;
  derived: DraftDerived | null;
  field: string;
}

/** A series the API derives: its op, its settings and its sources. */
export interface DraftDerived {
  op: string;
  parameters: DatasetParameters;
  a: DraftSource;
  b: DraftSource | null;
}

/** One layer: a dataset query or a derived series, drawn as one mark. `key` is its identity across moves; it is not sent. */
export interface DraftLayer {
  key: number;
  id: string;
  title?: string;
  dataset: string | null;
  parameters: DatasetParameters;
  derived: DraftDerived | null;
  mark: string;
  bindings: Bindings;
  range: SpecRange | null;
}

export interface DraftTrack {
  key: number;
  id: string;
  title?: string;
  /** The track's share of the chart's height; undefined for the API's default. */
  height?: number;
  layers: DraftLayer[];
}

/** One panel: a chart of tracks of layers, or one dataset as figures, a table or two periods. */
export interface DraftPanel {
  key: number;
  id: string;
  title?: string;
  kind: string;
  range: SpecRange | null;
  /** A chart's grid: minutes, 0 for every reading, null for each dataset's own spacing. */
  grid: number | null;
  tracks: DraftTrack[];
  dataset: string;
  parameters: DatasetParameters;
  columns?: string[];
}

/** The track heights the builder offers, as shares of the chart's height. */
export const TRACK_HEIGHTS = { small: 0.5, normal: 1, large: 2 } as const;

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

function text(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

/** Whether two ranges are the same; a normalised spec writes the report's range into each panel that takes it. */
function sameRange(a: SpecRange | null, b: SpecRange | null): boolean {
  return a !== null && b !== null && JSON.stringify(a) === JSON.stringify(b);
}

/** A copy without the keys whose value is undefined or null, so the spec sends only what was chosen. */
function compact(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(compact);
  return isObject(value) ? compactObject(value) : value;
}

function compactObject(value: object): Record<string, unknown> {
  const entries: [string, unknown][] = Object.entries(value);
  return Object.fromEntries(
    entries
      .filter(([, v]) => v !== undefined && v !== null)
      .map(([k, v]) => [k, compact(v)]),
  );
}

function splitDataset(value: unknown): { name: string | null; parameters: DatasetParameters } {
  if (!isObject(value)) return { name: null, parameters: {} };
  const { name, ...parameters } = value;
  return { name: typeof name === "string" ? name : null, parameters };
}

function readSource(value: unknown): DraftSource {
  const source = isObject(value) ? value : {};
  const { name, parameters } = splitDataset(source.dataset);
  return { dataset: name, parameters, derived: isObject(source.derived) ? readDerived(source.derived) : null, field: text(source.field) ?? "" };
}

function readDerived(value: Record<string, unknown>): DraftDerived {
  const { op, a, b, ...parameters } = value;
  return { op: text(op) ?? "", parameters, a: readSource(a), b: b === undefined || b === null ? null : readSource(b) };
}

function sourceJson(source: DraftSource): Record<string, unknown> {
  return source.derived
    ? { derived: derivedJson(source.derived), field: source.field }
    : { dataset: { name: source.dataset, ...source.parameters }, field: source.field };
}

function derivedJson(derived: DraftDerived): Record<string, unknown> {
  return { op: derived.op, ...derived.parameters, a: sourceJson(derived.a), b: derived.b ? sourceJson(derived.b) : undefined };
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

  get catalogue(): CustomReportCatalogue {
    return this.#catalogue;
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

  dataset(name: string | null | undefined): CustomReportDataset | undefined {
    return this.#catalogue.datasets.find((d) => d.name === name);
  }

  op(name: string | null | undefined): CustomReportOp | undefined {
    return this.#catalogue.ops.find((o) => o.name === name);
  }

  /** The datasets a chart layer may draw. */
  get chartDatasets(): CustomReportDataset[] {
    return this.#catalogue.datasets.filter((d) => d.marks.length > 0);
  }

  /** The datasets a panel of `kind` may show. */
  datasetsFor(kind: string): CustomReportDataset[] {
    return kind === "chart" ? this.chartDatasets : this.#catalogue.datasets.filter((d) => d.panelKinds.some((k) => k.name === kind));
  }

  layerCount(panel: DraftPanel): number {
    return panel.tracks.reduce((sum, t) => sum + t.layers.length, 0);
  }

  atLayerLimit(panel: DraftPanel): boolean {
    return this.layerCount(panel) >= this.#catalogue.maxLayers;
  }

  atTrackLimit(panel: DraftPanel): boolean {
    return panel.tracks.length >= this.#catalogue.maxTracks;
  }

  /** The marks a layer may be drawn as, from the catalogue: its dataset's, or its op's. */
  marksOf(layer: DraftLayer): { name: string; defaultBindings: CustomReportLayerBindings }[] {
    return layer.derived ? (this.op(layer.derived.op)?.marks ?? []) : (this.dataset(layer.dataset)?.marks ?? []);
  }

  #load(spec: Record<string, unknown>) {
    this.title = typeof spec.title === "string" ? spec.title : "";
    this.range = readRange(spec.range);
    const panels = Array.isArray(spec.panels) ? spec.panels : [];
    this.panels = panels.filter(isObject).map((p, i) => {
      const { name, parameters } = splitDataset(p.dataset);
      const tracks = Array.isArray(p.tracks) ? p.tracks.filter(isObject) : [];
      return {
        key: nextKey++,
        id: text(p.id) ?? `p${i + 1}`,
        title: text(p.title),
        kind: text(p.kind) ?? "chart",
        range: sameRange(readRange(p.range), this.range) ? null : readRange(p.range),
        grid: typeof p.grid === "number" ? p.grid : null,
        dataset: name ?? "",
        parameters,
        columns: isObject(p.bindings) ? strings(p.bindings.columns) : undefined,
        tracks: tracks.map((t, ti) => ({
          key: nextKey++,
          id: text(t.id) ?? `t${ti + 1}`,
          title: text(t.title),
          height: typeof t.height === "number" ? t.height : undefined,
          layers: (Array.isArray(t.layers) ? t.layers.filter(isObject) : []).map((l, li) => {
            const own = splitDataset(l.dataset);
            return {
              key: nextKey++,
              id: text(l.id) ?? `l${li + 1}`,
              title: text(l.title),
              dataset: own.name,
              parameters: own.parameters,
              derived: isObject(l.derived) ? readDerived(l.derived) : null,
              mark: text(l.mark) ?? "",
              bindings: isObject(l.bindings) ? compactObject(l.bindings) : {},
              range: readRange(l.range),
            };
          }),
        })),
      };
    });
  }

  /** The spec to run or save. */
  toSpec(): Record<string, unknown> {
    const panels = this.panels.map((p) => {
      const base = { id: p.id, title: p.title?.trim() ? p.title.trim() : undefined, kind: p.kind, range: p.range ?? undefined };
      if (p.kind !== "chart")
        return { ...base, dataset: { name: p.dataset, ...p.parameters }, bindings: p.columns ? { columns: p.columns } : undefined };
      return {
        ...base,
        grid: p.grid ?? undefined,
        tracks: p.tracks.map((t) => ({
          id: t.id,
          title: t.title?.trim() ? t.title.trim() : undefined,
          height: t.height,
          layers: t.layers.map((l) => ({
            id: l.id,
            title: l.title?.trim() ? l.title.trim() : undefined,
            dataset: l.derived ? undefined : { name: l.dataset, ...l.parameters },
            derived: l.derived ? derivedJson(l.derived) : undefined,
            mark: l.mark || undefined,
            bindings: Object.keys(l.bindings).length > 0 ? l.bindings : undefined,
            range: l.range ?? undefined,
          })),
        })),
      };
    });
    return compactObject({
      version: 1,
      title: this.title.trim() === "" ? undefined : this.title.trim(),
      range: this.range ?? undefined,
      panels,
    });
  }

  #freeId(taken: string[], prefix: string): string {
    for (let n = taken.length + 1; ; n++) if (!taken.includes(`${prefix}${n}`)) return `${prefix}${n}`;
  }

  /** A layer of `datasetName`, or of the first chart dataset the caller may read, drawn its first way. */
  #newLayer(panel: DraftPanel, datasetName?: string | null): DraftLayer {
    const datasets = this.chartDatasets;
    const dataset = datasets.find((d) => d.name === datasetName) ?? datasets.find((d) => d.permitted) ?? datasets[0];
    const mark = dataset?.marks[0];
    return {
      key: nextKey++,
      id: this.#freeId(panel.tracks.flatMap((t) => t.layers.map((l) => l.id)), "l"),
      dataset: dataset?.name ?? null,
      parameters: {},
      derived: null,
      mark: mark?.name ?? "",
      bindings: compactObject({ ...(mark?.defaultBindings ?? {}) }),
      range: null,
    };
  }

  /** A chart panel of one track of one layer. */
  addPanel(datasetName?: string) {
    if (this.atPanelLimit) return;
    const panel: DraftPanel = {
      key: nextKey++,
      id: this.#freeId(this.panels.map((p) => p.id), "p"),
      kind: "chart",
      range: null,
      grid: null,
      tracks: [],
      dataset: "",
      parameters: {},
    };
    const layer = this.#newLayer(panel, datasetName);
    panel.title = this.dataset(layer.dataset)?.label;
    panel.tracks = [{ key: nextKey++, id: "t1", layers: [layer] }];
    this.panels.push(panel);
  }

  removePanel(index: number) {
    this.panels.splice(index, 1);
  }

  movePanel(index: number, by: -1 | 1) {
    move(this.panels, index, by);
  }

  /**
   * Another kind of panel. A chart takes a track of a layer of the panel's dataset where a chart can
   * draw it; figures, a table or a comparison take the chart's first dataset where it can be shown so,
   * else the first that can.
   */
  setKind(index: number, kind: string) {
    const panel = this.panels[index];
    if (!panel || panel.kind === kind) return;
    if (kind === "chart") {
      panel.kind = kind;
      const layer = this.#newLayer(panel, this.dataset(panel.dataset)?.marks.length ? panel.dataset : null);
      if (layer.dataset === panel.dataset) layer.parameters = panel.parameters;
      panel.tracks = [{ key: nextKey++, id: "t1", layers: [layer] }];
      return;
    }
    const first = panel.tracks.flatMap((t) => t.layers).find((l) => l.dataset)?.dataset ?? panel.dataset;
    const dataset = this.datasetsFor(kind).find((d) => d.name === first) ?? this.datasetsFor(kind).find((d) => d.permitted) ?? this.datasetsFor(kind)[0];
    panel.kind = kind;
    panel.tracks = [];
    panel.grid = null;
    if (dataset && dataset.name !== panel.dataset) {
      panel.dataset = dataset.name;
      panel.parameters = {};
    }
    panel.columns = undefined;
  }

  /** Another dataset for a panel of figures, a table or a comparison: its parameters and columns start afresh. */
  setDataset(index: number, name: string) {
    const panel = this.panels[index];
    const dataset = this.dataset(name);
    if (!panel || !dataset) return;
    if (!panel.title?.trim() || panel.title === this.dataset(panel.dataset)?.label) panel.title = dataset.label;
    panel.dataset = name;
    panel.parameters = {};
    panel.columns = undefined;
  }

  setParameters(index: number, parameters: DatasetParameters) {
    const panel = this.panels[index];
    if (panel) panel.parameters = parameters;
  }

  setColumns(index: number, columns: string[] | undefined) {
    const panel = this.panels[index];
    if (panel) panel.columns = columns;
  }

  setPanelRange(index: number, range: SpecRange | null) {
    const panel = this.panels[index];
    if (panel) panel.range = range;
  }

  setPanelTitle(index: number, title: string) {
    const panel = this.panels[index];
    if (panel) panel.title = title;
  }

  setGrid(index: number, grid: number | null) {
    const panel = this.panels[index];
    if (panel) panel.grid = grid;
  }

  addTrack(index: number) {
    const panel = this.panels[index];
    if (!panel || this.atTrackLimit(panel) || this.atLayerLimit(panel)) return;
    panel.tracks.push({ key: nextKey++, id: this.#freeId(panel.tracks.map((t) => t.id), "t"), layers: [this.#newLayer(panel)] });
  }

  removeTrack(index: number, track: number) {
    const panel = this.panels[index];
    if (panel && panel.tracks.length > 1) panel.tracks.splice(track, 1);
  }

  moveTrack(index: number, track: number, by: -1 | 1) {
    const panel = this.panels[index];
    if (panel) move(panel.tracks, track, by);
  }

  setTrackTitle(index: number, track: number, title: string) {
    const t = this.panels[index]?.tracks[track];
    if (t) t.title = title;
  }

  setTrackHeight(index: number, track: number, height: number | undefined) {
    const t = this.panels[index]?.tracks[track];
    if (t) t.height = height;
  }

  layer(index: number, track: number, layer: number): DraftLayer | undefined {
    return this.panels[index]?.tracks[track]?.layers[layer];
  }

  addLayer(index: number, track: number) {
    const panel = this.panels[index];
    if (!panel || this.atLayerLimit(panel)) return;
    panel.tracks[track]?.layers.push(this.#newLayer(panel));
  }

  removeLayer(index: number, track: number, layer: number) {
    const t = this.panels[index]?.tracks[track];
    if (t && t.layers.length > 1) t.layers.splice(layer, 1);
  }

  moveLayer(index: number, track: number, layer: number, by: -1 | 1) {
    const t = this.panels[index]?.tracks[track];
    if (t) move(t.layers, layer, by);
  }

  setLayerTitle(index: number, track: number, layer: number, title: string) {
    const l = this.layer(index, track, layer);
    if (l) l.title = title;
  }

  /** A layer of another dataset: its parameters start afresh, its mark is the dataset's first with that mark's bindings. */
  setLayerDataset(index: number, track: number, layer: number, name: string) {
    const l = this.layer(index, track, layer);
    const dataset = this.dataset(name);
    if (!l || !dataset) return;
    l.dataset = name;
    l.derived = null;
    l.parameters = {};
    this.#mark(l, dataset.marks.find((m) => m.name === l.mark) ?? dataset.marks[0]);
  }

  /**
   * A layer derived by `op`: its sources start as the query of the panel's first dataset layer (or
   * the first chart dataset) and its first field an op may use, so a member starts from data the
   * chart already shows.
   */
  setLayerOp(index: number, track: number, layer: number, op: string) {
    const panel = this.panels[index];
    const l = this.layer(index, track, layer);
    const found = this.op(op);
    if (!panel || !l || !found) return;
    const start = this.#startingSource(panel, found);
    l.derived = {
      op,
      parameters: l.derived?.op === op ? l.derived.parameters : {},
      a: l.derived?.a ?? start,
      b: found.sources === 2 ? (l.derived?.b ?? { ...start, parameters: { ...start.parameters } }) : null,
    };
    l.dataset = null;
    l.parameters = {};
    this.#mark(l, found.marks.find((m) => m.name === l.mark) ?? found.marks[0]);
  }

  #startingSource(panel: DraftPanel, op: CustomReportOp): DraftSource {
    const layers = panel.tracks.flatMap((t) => t.layers).filter((l) => l.dataset);
    for (const layer of layers) {
      const field = this.sourceFields(layer.dataset, op)[0];
      if (field) return { dataset: layer.dataset, parameters: { ...layer.parameters }, derived: null, field };
    }
    const dataset = this.sourceDatasets(op)[0];
    return { dataset: dataset?.name ?? null, parameters: {}, derived: null, field: dataset ? (this.sourceFields(dataset.name, op)[0] ?? "") : "" };
  }

  /** The datasets a source of `op` may read: ones whose layers lie along an axis the op takes. */
  sourceDatasets(op: CustomReportOp): CustomReportDataset[] {
    return this.chartDatasets.filter((d) => d.xKinds.some((k) => op.sourceXKinds.includes(k)));
  }

  /** The fields of a dataset a source may use: numbers that fold onto a grid. */
  sourceFields(datasetName: string | null, op: CustomReportOp): string[] {
    return (this.dataset(datasetName)?.fields ?? [])
      .filter((f) => op.sourceFieldKinds.includes(f.kind) && f.aggregate)
      .map((f) => f.name);
  }

  setSource(index: number, track: number, layer: number, which: "a" | "b", source: DraftSource) {
    const l = this.layer(index, track, layer);
    if (!l?.derived) return;
    if (which === "a") l.derived.a = source;
    else l.derived.b = source;
  }

  /** A source that reads what another layer of the panel reads, with that layer's first drawn field where a source may use it. */
  copySource(index: number, track: number, layer: number, which: "a" | "b", from: DraftLayer) {
    const l = this.layer(index, track, layer);
    const op = this.op(l?.derived?.op);
    if (!l?.derived || !op || !from.dataset) return;
    const usable = this.sourceFields(from.dataset, op);
    const drawn = strings(from.bindings.y)?.find((f) => usable.includes(f)) ?? usable[0] ?? "";
    this.setSource(index, track, layer, which, { dataset: from.dataset, parameters: { ...from.parameters }, derived: null, field: drawn });
  }

  setOpParameters(index: number, track: number, layer: number, parameters: DatasetParameters) {
    const l = this.layer(index, track, layer);
    if (l?.derived) l.derived.parameters = parameters;
  }

  setLayerParameters(index: number, track: number, layer: number, parameters: DatasetParameters) {
    const l = this.layer(index, track, layer);
    if (l) l.parameters = parameters;
  }

  /** Another mark: the bindings are reset to the ones the catalogue gives it. */
  setLayerMark(index: number, track: number, layer: number, name: string) {
    const l = this.layer(index, track, layer);
    const mark = l ? this.marksOf(l).find((m) => m.name === name) : undefined;
    if (l && mark) this.#mark(l, mark);
  }

  #mark(layer: DraftLayer, mark: { name: string; defaultBindings: CustomReportLayerBindings } | undefined) {
    layer.mark = mark?.name ?? "";
    layer.bindings = compactObject({ ...(mark?.defaultBindings ?? {}) });
  }

  setLayerBinding(index: number, track: number, layer: number, key: string, value: unknown) {
    const l = this.layer(index, track, layer);
    if (l) l.bindings = compactObject({ ...l.bindings, [key]: value });
  }

  resetLayerBindings(index: number, track: number, layer: number) {
    const l = this.layer(index, track, layer);
    if (l) this.#mark(l, this.marksOf(l).find((m) => m.name === l.mark));
  }

  setLayerRange(index: number, track: number, layer: number, range: SpecRange | null) {
    const l = this.layer(index, track, layer);
    if (l) l.range = range;
  }
}

function move<T>(list: T[], index: number, by: -1 | 1) {
  const to = index + by;
  if (to < 0 || to >= list.length) return;
  const [item] = list.splice(index, 1);
  list.splice(to, 0, item);
}
