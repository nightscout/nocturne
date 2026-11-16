import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import type { LensId, SignalId, Vocabulary } from "./catalogue-vocabulary.js";
import {
  addCalculated,
  addSignalLayer,
  allLayers,
  asObject,
  axisOf,
  copyJson,
  givenCaption,
  givenColumns,
  givenKind,
  givenLayers,
  givenMaxHours,
  givenReads,
  lensForAxis,
  ownScaleReason,
  removeLayer,
  removeLayerReason,
  renameLayer,
  setLayerMark,
  setLayerRange,
  setOwnScale,
  toggleGivenColumn,
  tracksOf,
  type CalculatedOp,
  type GivenLayer,
  type GivenSource,
  type Json,
} from "./given.svelte.js";

/** The kinds of block a member adds to a report. */
export type BlockKind = "chart" | "figures" | "table" | "comparison";

/** A dataset a block of figures or a table reads. */
export interface Source {
  dataset: string;
}
import { accessReason, rangeLabel, signalLabel, sourceLabel, starterWords } from "./vocabulary-words.svelte.js";
import { parseSavedReport, withoutNulls, type SavedBlock, type SavedReport } from "./saved-state.js";
import {
  Workspace,
  eventParameters,
  freshKey,
  reserveKey,
  vocabularyOf,
  momentText,
  rangeText,
  upperFirst,
  type Built,
  type DateSpan,
  type RangeOption,
  type Reason,
  type Starter,
  type WorkspaceState,
} from "./workspace.svelte.js";

interface BlockBase {
  key: number;
  /** The block's own range in hours, or null for the report's. */
  range: number | null;
}

export interface ChartBlock extends BlockBase {
  kind: "chart";
  ws: Workspace;
}

export interface FiguresBlock extends BlockBase {
  kind: "figures";
  title: string;
  source: string;
  columns: string[];
  /** The panel id of a spec the block was opened from; absent for a block the member added. */
  panelId?: string;
}

export interface TableBlock extends BlockBase {
  kind: "table";
  title: string;
  source: string;
  columns: string[];
  /** The groups of events of an "each event" table, edited as a chart's moments are. */
  events: Workspace;
  panelId?: string;
}

export interface ComparisonBlock extends BlockBase {
  kind: "comparison";
  title: string;
  /** A preset pair of periods, or null for the two periods given. */
  preset: string | null;
  a: DateSpan | null;
  b: DateSpan | null;
  panelId?: string;
}

/**
 * A panel held as the spec gives it, of any kind: one the signal model does not make (see
 * given.svelte.ts). Its range is the panel's own, so `range` is always null.
 */
export interface GivenBlock extends BlockBase {
  kind: "given";
  panel: Json;
}

export type Block = ChartBlock | FiguresBlock | TableBlock | ComparisonBlock | GivenBlock;

/** A report range that is not the last N hours: none (each panel's own or its dataset's default), or two instants. */
export type PinnedRange = "none" | { from: string; to: string };

export interface ReportState {
  title: string;
  rangeHours: number;
  /** The range a spec gave that the presets cannot state; null once the member picks a preset. */
  pinned: PinnedRange | null;
  blocks: Block[];
  /** The key of the block open for editing, or null for none. */
  open: number | null;
}

/** What a block shows, as the member reads it. */
export function shownKind(block: Block): BlockKind {
  return block.kind === "given" ? givenKind(block.panel) : block.kind;
}

/** The option a range control shows for a range a spec gave that is not a preset. */
export const AS_SAVED = "saved";

export interface Column {
  field: string;
  label: string;
  on: boolean;
  /** Why the column cannot be switched now, or null. */
  reason: Reason;
}

export interface Choice {
  value: string;
  label: string;
}

/** The block range choices: the report's first, then the presets. */
export interface BlockRangeOption {
  /** "report" for the report's range, else a preset id. */
  id: string;
  label: string;
  hours: number | null;
  reason: Reason;
}

export const SAME_AS_REPORT = "report";

interface Footprint {
  panels: number;
  reads: string[];
  comparison: boolean;
}

const COMPARISON_FALLBACK_PRESET = "last14-prior14";

/**
 * A report as the member builds it: a title, a range, and a stack of blocks
 * (charts, figures, tables and comparisons), one open for editing. Every
 * change has a reason it cannot be made now, read before the control is used.
 */
export class Report {
  state = $state<ReportState>({
    title: "",
    rangeHours: 0,
    pinned: null,
    blocks: [],
    open: null,
  });

  /**
   * The line a block shows when it took a shorter range than the report's because its data covers no more;
   * cleared by the next block added. Not saved.
   */
  notice = $state<{ key: number; text: string } | null>(null);

  readonly #catalogue: CustomReportCatalogue;
  readonly #vocab: Vocabulary;
  readonly #clock: (minute: number) => string;

  /** How captions write two dates: the app's date format on a page. */
  readonly dates: (span: DateSpan) => string;

  constructor(
    catalogue: CustomReportCatalogue,
    starter: Starter | null = null,
    clock?: (minute: number) => string,
    dates?: (span: DateSpan) => string
  ) {
    this.#catalogue = catalogue;
    this.#vocab = vocabularyOf(catalogue);
    this.#clock = clock ?? ((m) => String(m));
    this.dates = dates ?? ((span) => `${span.from} to ${span.to}`);
    this.state = {
      title: starter ? (starterWords(this.#clock)[starter.id]?.title ?? "") : "",
      rangeHours: starter?.rangeHours ?? this.#vocab.defaultReportHours,
      pinned: null,
      blocks: [],
      open: null,
    };
    const chart = this.#chart();
    if (starter) chart.ws.load(starter, false);
    if (starter) chart.ws.title = "";
    this.state.blocks.push(chart);
    this.state.open = chart.key;
  }

  get catalogue(): CustomReportCatalogue {
    return this.#catalogue;
  }

  get vocab(): Vocabulary {
    return this.#vocab;
  }

  get title(): string {
    return this.state.title;
  }

  set title(value: string) {
    this.state.title = value.slice(0, this.#catalogue.maxTitleLength);
  }

  get rangeHours(): number {
    return this.state.rangeHours;
  }

  get blocks(): Block[] {
    return this.state.blocks;
  }

  get open(): number | null {
    return this.state.open;
  }

  #chart(key = freshKey()): ChartBlock {
    const ws = new Workspace(this.#catalogue, undefined, {
      reportHours: () => this.state.rangeHours,
      budget: (built) => this.#chartBudget(key, built),
    });
    ws.clock = this.#clock;
    ws.dates = (span) => this.dates(span);
    return { kind: "chart", key, range: null, ws };
  }

  #eventsEditor(state?: WorkspaceState): Workspace {
    const ws = new Workspace(this.#catalogue);
    ws.clock = this.#clock;
    ws.dates = (span) => this.dates(span);
    if (state) ws.state = structuredClone(state);
    else ws.setLens("event");
    return ws;
  }

  /** The report's state as the workspace saves it with the spec: ids and choices, nothing derived. */
  toState(): SavedReport {
    const blocks = this.state.blocks.map((block): SavedBlock => {
      switch (block.kind) {
        case "chart":
          return { kind: "chart", key: block.key, chart: $state.snapshot(block.ws.state) };
        case "figures":
          return {
            kind: "figures",
            key: block.key,
            range: block.range,
            title: block.title,
            source: block.source,
            columns: [...block.columns],
            panelId: block.panelId,
          };
        case "table":
          return {
            kind: "table",
            key: block.key,
            range: block.range,
            title: block.title,
            source: block.source,
            columns: [...block.columns],
            ...(block.source === this.#vocab.eventDataset ? { events: $state.snapshot(block.events.state) } : {}),
            panelId: block.panelId,
          };
        case "comparison":
          return {
            kind: "comparison",
            key: block.key,
            range: block.range,
            title: block.title,
            preset: block.preset,
            a: block.a && { ...block.a },
            b: block.b && { ...block.b },
            panelId: block.panelId,
          };
        case "given":
          return { kind: "given", key: block.key, panel: copyJson(block.panel) };
      }
    });
    const pinned = this.state.pinned === null ? undefined : $state.snapshot(this.state.pinned);
    // A JSON round trip drops the keys an optional narrowing leaves undefined, as the saved form has them.
    return JSON.parse(JSON.stringify({ title: this.state.title, rangeHours: this.state.rangeHours, pinnedRange: pinned, blocks }));
  }

  /**
   * The report a spec describes, block by block: a panel of figures, a table or a comparison that a
   * block of that kind makes exactly opens as one, and every other panel is held as given. Null when
   * the spec has no panels to read. `mapped: false` holds every panel as given.
   */
  static fromSpec(
    catalogue: CustomReportCatalogue,
    spec: unknown,
    clock?: (minute: number) => string,
    dates?: (span: DateSpan) => string,
    mapped = true
  ): Report | null {
    const given = asObject(spec);
    const panels = Array.isArray(given?.panels) ? given.panels.map(asObject) : [];
    if (!given || panels.length === 0 || panels.some((p) => p === null)) return null;
    const report = new Report(catalogue, null, clock, dates);
    const range = asObject(given.range);
    const hours = typeof range?.hours === "number" ? range.hours : null;
    const from = typeof range?.from === "string" ? range.from : null;
    const to = typeof range?.to === "string" ? range.to : null;
    const blocks = panels.map((p) => {
      const panel = copyJson(p ?? {});
      return (mapped ? report.#mapped(panel) : null) ?? { kind: "given" as const, key: freshKey(), range: null, panel };
    });
    report.state = {
      title: typeof given.title === "string" ? given.title.slice(0, catalogue.maxTitleLength) : "",
      rangeHours: hours ?? (from && to ? Report.#hoursBetween(from, to) : report.#vocab.defaultReportHours),
      pinned: hours !== null ? null : from && to ? { from, to } : "none",
      blocks,
      open: blocks[0]?.key ?? null,
    };
    return report;
  }

  static #hoursBetween(from: string, to: string): number {
    const hours = Math.round((Date.parse(to) - Date.parse(from)) / 3_600_000);
    return Number.isFinite(hours) && hours > 0 ? hours : 24;
  }

  /** The block of figures, table or comparison that makes `panel` exactly, or null when none does. */
  #mapped(panel: Json): Block | null {
    const clean = asObject(withoutNulls(panel)) ?? {};
    const id = typeof clean.id === "string" ? clean.id : undefined;
    const title = typeof clean.title === "string" ? clean.title : "";
    if (title !== title.trim()) return null;
    const range = asObject(clean.range);
    const keys = Object.keys(range ?? {});
    const hours = range && keys.length === 1 && typeof range.hours === "number" ? range.hours : null;
    if (range && hours === null) return null;
    const dataset = asObject(clean.dataset) ?? {};
    const name = typeof dataset.name === "string" ? dataset.name : "";
    const extra = Object.keys(dataset).filter((k) => k !== "name");
    const allowed = ["id", "title", "kind", "range", "dataset", "bindings"];
    if (Object.keys(clean).some((k) => !allowed.includes(k))) return null;
    const bindings = asObject(clean.bindings);
    const columns = Array.isArray(bindings?.columns) ? bindings.columns.filter((c): c is string => typeof c === "string") : null;
    const key = freshKey();
    if (clean.kind === "comparison") {
      if (range || bindings || name !== this.#vocab.comparisonDataset) return null;
      const span = (v: unknown): DateSpan | null => {
        const o = asObject(v);
        return o && typeof o.from === "string" && typeof o.to === "string" && Object.keys(o).length === 2 ? { from: o.from, to: o.to } : null;
      };
      const preset = typeof dataset.preset === "string" ? dataset.preset : null;
      if (preset && extra.length === 1) return { kind: "comparison", key, range: null, title, preset, a: null, b: null, panelId: id };
      const a = span(dataset.a);
      const b = span(dataset.b);
      if (!preset && a && b && extra.length === 2) return { kind: "comparison", key, range: null, title, preset: null, a, b, panelId: id };
      return null;
    }
    if (extra.length > 0 || !columns || columns.length === 0 || Object.keys(bindings ?? {}).length !== 1) return null;
    const kind = clean.kind === "stat_tiles" ? "stat_tiles" : clean.kind === "table" ? "table" : null;
    if (!kind) return null;
    const sources = kind === "stat_tiles" ? this.#vocab.figureSources : this.#vocab.tableSources;
    if (!sources.includes(name) || name === this.#vocab.eventDataset) return null;
    const offered = this.#defaultColumns(name, kind);
    if (columns.some((c, i) => columns.indexOf(c) !== i || !offered.includes(c))) return null;
    return kind === "stat_tiles"
      ? { kind: "figures", key, range: hours, title, source: name, columns, panelId: id }
      : { kind: "table", key, range: hours, title, source: name, columns, events: this.#eventsEditor(), panelId: id };
  }

  /**
   * The report a saved state describes, its blocks keeping their keys so the spec keeps its panel
   * ids; null when the state is of another version or shape (see `parseSavedReport`).
   */
  static fromState(
    catalogue: CustomReportCatalogue,
    version: unknown,
    state: unknown,
    clock?: (minute: number) => string,
    dates?: (span: DateSpan) => string
  ): Report | null {
    const saved = parseSavedReport(version, state, vocabularyOf(catalogue));
    if (!saved || !Report.#withinLimits(catalogue, saved)) return null;
    for (const block of saved.blocks) {
      reserveKey(block.key);
      const inner = block.kind === "chart" ? block.chart : block.kind === "table" ? block.events : undefined;
      for (const m of inner?.moments ?? []) reserveKey(m.key);
      for (const c of inner?.combos ?? []) reserveKey(c.key);
    }
    const report = new Report(catalogue, null, clock, dates);
    report.#restore(saved);
    return report;
  }

  /** Whether the saved titles fit the catalogue's limit, as every title the workspace keeps does. */
  static #withinLimits(catalogue: CustomReportCatalogue, saved: SavedReport): boolean {
    const titles = [
      saved.title,
      ...saved.blocks.map((b) => (b.kind === "chart" ? b.chart.title : b.kind === "given" ? "" : b.title)),
      ...saved.blocks.flatMap((b) => (b.kind === "table" && b.events ? [b.events.title] : [])),
    ];
    return titles.every((t) => t.length <= catalogue.maxTitleLength);
  }

  #restore(saved: SavedReport) {
    const blocks = saved.blocks.map((b): Block => {
      switch (b.kind) {
        case "chart": {
          const chart = this.#chart(b.key);
          chart.ws.state = structuredClone(b.chart);
          return chart;
        }
        case "figures":
          return { ...b, columns: [...b.columns] };
        case "table":
          return {
            kind: "table",
            key: b.key,
            range: b.range,
            title: b.title,
            source: b.source,
            columns: [...b.columns],
            events: this.#eventsEditor(b.events),
            panelId: b.panelId,
          };
        case "comparison":
          return { ...b };
        case "given":
          return { kind: "given", key: b.key, range: null, panel: copyJson(b.panel) };
      }
    });
    this.state = {
      title: saved.title,
      rangeHours: saved.rangeHours,
      pinned: saved.pinnedRange ?? null,
      blocks,
      open: blocks[0]?.key ?? null,
    };
  }

  /** The range a block reads, its own or the report's. */
  hoursOf(block: Block): number {
    if (block.kind === "chart") return block.ws.rangeHours;
    if (block.kind === "given") {
      const range = asObject(block.panel.range);
      if (typeof range?.hours === "number") return range.hours;
      if (typeof range?.from === "string" && typeof range.to === "string") return Report.#hoursBetween(range.from, range.to);
    }
    return block.range ?? this.state.rangeHours;
  }

  /** The report's range as a spec writes it: the pinned range a spec gave, else the last N hours. */
  #specRange(): Record<string, unknown> {
    const pinned = this.state.pinned;
    if (pinned === "none") return {};
    if (pinned) return { range: { from: pinned.from, to: pinned.to } };
    return { range: { hours: this.state.rangeHours } };
  }

  // Panels and limits

  #dataset(name: string) {
    return this.#catalogue.datasets.find((d) => d.name === name);
  }

  /** A block's panels as its own preview runs them: ids as the block gives them, no range. */
  panelsOf(block: Block): Record<string, unknown>[] {
    switch (block.kind) {
      case "chart":
        return block.ws.built.panels;
      case "figures":
        return [
          {
            id: "figures",
            kind: "stat_tiles",
            ...(block.title.trim() ? { title: block.title.trim() } : {}),
            dataset: { name: block.source },
            bindings: { columns: block.columns },
          },
        ];
      case "table":
        return [
          {
            id: "table",
            kind: "table",
            ...(block.title.trim() ? { title: block.title.trim() } : {}),
            dataset: {
              name: block.source,
              ...(block.source === this.#vocab.eventDataset
                ? eventParameters(block.events.state, this.#vocab, block.events.leaveOutDefaults
                  )
                : {}),
            },
            bindings: { columns: block.columns },
          },
        ];
      case "comparison":
        return [
          {
            id: "comparison",
            kind: "comparison",
            ...(block.title.trim() ? { title: block.title.trim() } : {}),
            dataset: {
              name: this.#vocab.comparisonDataset,
              ...(block.preset
                ? { preset: block.preset }
                : block.a && block.b
                  ? { a: block.a, b: block.b }
                  : {}),
            },
          },
        ];
      case "given":
        return [block.panel];
    }
  }

  /** The spec a block's preview runs, or null while it draws nothing. */
  previewSpec(block: Block): Record<string, unknown> | null {
    const panels = this.panelsOf(block);
    if (panels.length === 0) return null;
    if (block.kind === "given") return { version: 1, ...this.#specRange(), panels: [copyJson(block.panel)] };
    return { version: 1, range: { hours: this.hoursOf(block) }, panels };
  }

  /**
   * What a block takes of the report's limits: its panels (an empty chart holds
   * its place as one), the keys of its reads as the run cache counts them, and
   * whether it is a comparison.
   */
  #footprint(block: Block, built?: Built, reportHours?: number): Footprint {
    const own = block.kind === "chart" ? block.ws.ownRange : block.range;
    const hours = own ?? reportHours ?? this.state.rangeHours;
    if (block.kind === "chart") {
      const b = built ?? block.ws.built;
      const chartHours = built ? built.hours : hours;
      return {
        panels: Math.max(1, b.panels.length),
        reads: b.reads.map((r) => `chart|${chartHours}|${r}`),
        comparison: false,
      };
    }
    if (block.kind === "given") {
      const own = typeof asObject(block.panel.range)?.hours === "number" || asObject(block.panel.range)?.from;
      const at = own ? this.hoursOf(block) : (reportHours ?? this.state.rangeHours);
      return {
        panels: 1,
        reads: givenReads(block.panel).map((r) => `given|${at}|${r}`),
        comparison: block.panel.kind === "comparison",
      };
    }
    return {
      panels: 1,
      reads: this.panelsOf(block).map(
        (p) => `${String(p.kind)}|${hours}|${JSON.stringify(p.dataset)}`
      ),
      comparison: block.kind === "comparison",
    };
  }

  /** Why blocks of these footprints are over a limit of the report; null when not. */
  #limit(footprints: Footprint[]): Reason {
    const c = this.#catalogue;
    const panels = footprints.reduce((n, f) => n + f.panels, 0);
    if (panels > c.maxPanels)
      return this.state.blocks.some((b) => b.kind === "chart" && b.ws.lens === "against" && b.ws.findLag)
        ? `A report has at most ${c.maxPanels} parts. The lag chart is a part of its own.`
        : `A report has at most ${c.maxPanels} parts.`;
    const all = footprints.flatMap((f) => f.reads);
    const reads = all.filter((r, i) => all.indexOf(r) === i);
    if (reads.length > c.maxReads)
      return `A report reads at most ${c.maxReads} sets of data. Remove a block or a signal first.`;
    const comparisons = footprints.filter((f) => f.comparison).length;
    const maxComparisons = this.#dataset(this.#vocab.comparisonDataset)?.maxReadsPerReport;
    if (maxComparisons !== undefined && comparisons > maxComparisons)
      return `A report has at most ${maxComparisons} comparisons.`;
    return null;
  }

  #footprints(reportHours?: number): Footprint[] {
    return this.state.blocks.map((b) =>
      this.#footprint(b, undefined, reportHours)
    );
  }

  #chartBudget(key: number, built: Built): Reason {
    return this.#limit(
      this.state.blocks.map((b) =>
        b.key === key ? this.#footprint(b, built) : this.#footprint(b)
      )
    );
  }

  // Blocks

  /**
   * The range a block reading `source` takes for `range` (null for the report's): the same, or the longest
   * preset the dataset covers when the asked range is longer.
   */
  #fittedRange(source: string, range: number | null): number | null {
    const max = this.#dataset(source)?.maxRangeHours;
    if (max === undefined || (range ?? this.state.rangeHours) <= max) return range;
    return this.#vocab.rangePresets.filter((p) => p.hours <= max).at(-1)?.hours ?? range;
  }

  /** The line saying a block reading `source` shows `hours` because its data covers no more. */
  #shorterText(source: string, hours: number): string {
    const days = Math.floor((this.#dataset(source)?.maxRangeHours ?? hours) / 24);
    return `${upperFirst(sourceLabel(source))} covers at most ${days} days, so this block shows ${rangeText(hours, this.#vocab.rangePresets).replace(/^over /, "")}.`;
  }

  #newBlock(kind: BlockKind): Block {
    const key = freshKey();
    switch (kind) {
      case "chart":
        return this.#chart();
      case "figures": {
        const source = this.figureSources[0]?.dataset ?? this.#vocab.figureSources[0] ?? "";
        return {
          kind,
          key,
          range: this.#fittedRange(source, null),
          title: "",
          source,
          columns: this.#defaultColumns(source, "stat_tiles"),
        };
      }
      case "table": {
        const source = this.tableSources[0]?.dataset ?? this.#vocab.tableSources[0] ?? "";
        return {
          kind,
          key,
          range: this.#fittedRange(source, null),
          title: "",
          source,
          columns: this.#defaultColumns(source, "table"),
          events: this.#eventsEditor(),
        };
      }
      case "comparison":
        return {
          kind,
          key,
          range: null,
          title: "",
          preset: this.comparisonPresets.some(
            (p) => p.value === COMPARISON_FALLBACK_PRESET
          )
            ? COMPARISON_FALLBACK_PRESET
            : (this.comparisonPresets[0]?.value ?? null),
          a: null,
          b: null,
        };
    }
  }

  /** What a new block of `kind` would take, without making one. */
  #newFootprint(kind: BlockKind): Footprint {
    const hours = this.state.rangeHours;
    const fitted = (source: string | undefined) => (source ? (this.#fittedRange(source, null) ?? hours) : hours);
    switch (kind) {
      case "chart":
        return { panels: 1, reads: [], comparison: false };
      case "figures": {
        const source = this.figureSources[0]?.dataset;
        return {
          panels: 1,
          reads: [`stat_tiles|${fitted(source)}|${JSON.stringify({ name: source })}`],
          comparison: false,
        };
      }
      case "table": {
        const source = this.tableSources[0]?.dataset;
        return {
          panels: 1,
          reads: [`table|${fitted(source)}|${JSON.stringify({ name: source })}`],
          comparison: false,
        };
      }
      case "comparison":
        return {
          panels: 1,
          reads: [`comparison|${hours}|new`],
          comparison: true,
        };
    }
  }

  /** Why a block of `kind` cannot be added now, or null. */
  addReason(kind: BlockKind): Reason {
    if (kind === "figures" && this.figureSources.length === 0)
      return "You cannot read the data for figures.";
    if (kind === "table" && this.tableSources.length === 0)
      return "You cannot read the data for a table.";
    if (
      kind === "comparison" &&
      !this.#dataset(this.#vocab.comparisonDataset)?.permitted
    )
      return "You cannot read the data for a comparison.";
    return this.#limit([...this.#footprints(), this.#newFootprint(kind)]);
  }

  /** Adds a block of `kind` at the end and opens it. */
  add(kind: BlockKind): Reason {
    const reason = this.addReason(kind);
    if (reason) return reason;
    const block = this.#newBlock(kind);
    this.state.blocks.push(block);
    this.state.open = block.key;
    this.notice =
      (block.kind === "figures" || block.kind === "table") && block.range !== null
        ? { key: block.key, text: this.#shorterText(block.source, block.range) }
        : null;
    return null;
  }

  /**
   * Adds a chart at the end that shows the signal under `lens`, and opens it. Its range is the report's, or the
   * longest preset the signal's data covers when the report's is longer. Nothing changes when it cannot be done.
   */
  addChartWith(id: SignalId, lens: LensId): Reason {
    const reason = this.addReason("chart");
    if (reason) return reason;
    const block = this.#chart();
    this.state.blocks.push(block);
    const limit = this.#vocab.cellOf(lens, id)?.maxRangeHours ?? this.state.rangeHours;
    const fits = this.#vocab.rangePresets.filter((p) => p.hours <= limit).at(-1);
    const placed =
      block.ws.setLens(lens) ??
      (limit < this.state.rangeHours && fits ? block.ws.setRange(fits.hours) : null) ??
      block.ws.toggleSignal(id);
    if (placed) {
      this.state.blocks.pop();
      return placed;
    }
    this.state.open = block.key;
    const own = block.ws.ownRange;
    this.notice =
      own !== null
        ? {
            key: block.key,
            text: `${upperFirst(signalLabel(id))} covers at most ${Math.floor(limit / 24)} days, so this chart shows ${rangeText(own, this.#vocab.rangePresets).replace(/^over /, "")}.`,
          }
        : null;
    return null;
  }

  remove(key: number) {
    const at = this.state.blocks.findIndex((b) => b.key === key);
    if (at < 0) return;
    this.state.blocks.splice(at, 1);
    if (this.state.open === key)
      this.state.open =
        this.state.blocks[Math.min(at, this.state.blocks.length - 1)]?.key ??
        null;
  }

  /**
   * Puts a removed block back at `at` and opens it, unless the report has no room for it now or its
   * range no longer fits the report's.
   */
  restoreBlock(block: Block, at: number): Reason {
    if (this.state.blocks.some((b) => b.key === block.key)) return null;
    const reason =
      (block.kind === "chart" ? null : this.#blockRangeReason(block, this.hoursOf(block))) ??
      this.#limit([...this.#footprints(), this.#footprint(block)]);
    if (reason) return reason;
    this.state.blocks.splice(Math.min(at, this.state.blocks.length), 0, block);
    this.state.open = block.key;
    return null;
  }

  /** Moves a block one place up (-1) or down (1). */
  move(key: number, by: -1 | 1) {
    const at = this.state.blocks.findIndex((b) => b.key === key);
    const to = at + by;
    if (at < 0 || to < 0 || to >= this.state.blocks.length) return;
    const [block] = this.state.blocks.splice(at, 1);
    this.state.blocks.splice(to, 0, block);
  }

  openBlock(key: number) {
    this.state.open = key;
  }

  // Ranges

  #blockRangeReason(block: Block, hours: number): Reason {
    if (block.kind === "comparison") return null;
    if (block.kind === "chart") return null;
    if (block.kind === "given") {
      if (block.panel.kind === "comparison") return null;
      const max = givenMaxHours(this.#catalogue, block.panel);
      return hours > max ? `${upperFirst(this.blockTitle(block).trim() || this.caption(block))} shows at most ${Math.floor(max / 24)} days. Choose a shorter range first.` : null;
    }
    const dataset = this.#dataset(block.source);
    if (dataset && hours > dataset.maxRangeHours)
      return `${this.sourceLabel(block)} shows at most ${Math.floor(dataset.maxRangeHours / 24)} days. Choose a shorter range first.`;
    return null;
  }

  /** The report's range presets, each with why it cannot be chosen now. */
  get ranges(): RangeOption[] {
    return this.#vocab.rangePresets.map((p) => ({ ...p, label: rangeLabel(p.id), reason: this.rangeReason(p.hours) }));
  }

  rangeReason(hours: number): Reason {
    for (const block of this.state.blocks) {
      if (block.kind === "given" ? asObject(block.panel.range) !== null : block.range !== null && block.kind !== "chart") continue;
      const reason =
        block.kind === "chart"
          ? block.ws.reportRangeReason(hours)
          : this.#blockRangeReason(block, hours);
      if (reason) return reason;
    }
    return this.#limit(this.#footprints(hours));
  }

  setRange(hours: number): Reason {
    const reason = this.rangeReason(hours);
    if (!reason) {
      this.state.rangeHours = hours;
      this.state.pinned = null;
    }
    return reason;
  }

  /** The report range a spec gave that no preset states, as words; null when the range is a preset. */
  get pinnedLabel(): string | null {
    const pinned = this.state.pinned;
    if (pinned === null) return null;
    if (pinned === "none") return "Each block's own range";
    return this.dates({ from: pinned.from.slice(0, 10), to: pinned.to.slice(0, 10) });
  }

  /** A given block's own range: null for the report's, hours, or two instants. */
  #givenRange(block: GivenBlock): { hours: number } | { from: string; to: string } | null {
    const range = asObject(block.panel.range);
    if (typeof range?.hours === "number") return { hours: range.hours };
    if (typeof range?.from === "string" && typeof range.to === "string") return { from: range.from, to: range.to };
    return null;
  }

  /** A block's range choices: the report's first, then the presets. */
  blockRanges(block: Block): BlockRangeOption[] {
    const report: BlockRangeOption = {
      id: SAME_AS_REPORT,
      label: "Same as the report",
      hours: null,
      reason:
        block.kind === "chart"
          ? block.ws.sameAsReportReason
          : this.#blockRangeReason(block, this.state.rangeHours),
    };
    const presets =
      block.kind === "chart"
        ? block.ws.ranges
        : this.#vocab.rangePresets.map((p) => ({
            ...p,
            reason:
              block.kind === "given"
                ? this.#withBlock(block, { panel: { ...block.panel, range: { hours: p.hours } } })
                : this.#withBlock(block, { range: p.hours }),
          }));
    const saved: BlockRangeOption[] =
      this.blockRangeId(block) === AS_SAVED ? [{ id: AS_SAVED, label: this.#savedRangeLabel(block), hours: null, reason: null }] : [];
    return [
      ...saved,
      report,
      ...presets.map((p) => ({
        id: p.id,
        label: rangeLabel(p.id),
        hours: p.hours,
        reason: p.reason,
      })),
    ];
  }

  #savedRangeLabel(block: Block): string {
    const range = block.kind === "given" ? this.#givenRange(block) : null;
    if (range && "from" in range) return this.dates({ from: range.from.slice(0, 10), to: range.to.slice(0, 10) });
    return rangeText(this.hoursOf(block), this.#vocab.rangePresets);
  }

  /** The id of a block's range choice; `AS_SAVED` for a range a spec gave that no preset states. */
  blockRangeId(block: Block): string {
    if (block.kind === "given") {
      const range = this.#givenRange(block);
      if (range === null) return SAME_AS_REPORT;
      if ("from" in range) return AS_SAVED;
      return this.#vocab.rangePresets.find((p) => p.hours === range.hours)?.id ?? AS_SAVED;
    }
    const own = block.kind === "chart" ? block.ws.ownRange : block.range;
    if (own === null) return SAME_AS_REPORT;
    return this.#vocab.rangePresets.find((p) => p.hours === own)?.id ?? "";
  }

  setBlockRange(block: Block, hours: number | null): Reason {
    if (this.notice?.key === block.key) this.notice = null;
    if (block.kind === "chart") return block.ws.setRange(hours);
    if (block.kind === "given") {
      const panel = { ...copyJson(block.panel), range: hours === null ? null : { hours } };
      const reason = this.#blockRangeReason(block, hours ?? this.state.rangeHours) ?? this.#withBlock(block, { panel });
      if (!reason) this.#update(block, { panel });
      return reason;
    }
    const reason =
      this.#blockRangeReason(block, hours ?? this.state.rangeHours) ??
      this.#withBlock(block, { range: hours });
    if (!reason) this.#update(block, { range: hours });
    return reason;
  }

  /** Why the block with `patch` applied does not fit the report, or null. */
  #withBlock(block: Block, patch: Partial<Block>): Reason {
    // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- a patch of the block's own kind
    const next = { ...block, ...patch } as Block;
    const own = this.#blockRangeReason(next, this.hoursOf(next));
    if (own) return own;
    return this.#limit(
      this.state.blocks.map((b) => this.#footprint(b.key === block.key ? next : b))
    );
  }

  #update(block: Block, patch: Partial<Block>) {
    const at = this.state.blocks.findIndex((b) => b.key === block.key);
    if (at >= 0) Object.assign(this.state.blocks[at], patch);
  }

  // Figures and tables

  #permittedSources(list: readonly string[], kind: string): Source[] {
    return list.map((dataset) => ({ dataset })).filter((s) => {
      const d = this.#dataset(s.dataset);
      return d?.permitted && d.panelKinds.some((k) => k.name === kind);
    });
  }

  get figureSources(): Source[] {
    return this.#permittedSources(this.#vocab.figureSources, "stat_tiles");
  }

  get tableSources(): Source[] {
    return this.#permittedSources(this.#vocab.tableSources, "table");
  }

  sourceLabel(block: FiguresBlock | TableBlock): string {
    return sourceLabel(block.source);
  }

  #defaultColumns(source: string, kind: string): string[] {
    return [
      ...(this.#dataset(source)?.panelKinds.find((k) => k.name === kind)
        ?.columns ?? []),
    ];
  }

  #fieldLabel(source: string, field: string): string {
    return (
      this.#dataset(source)?.fields.find((f) => f.name === field)?.label ??
      field
    );
  }

  /** Why the block cannot read `source` now, or null. */
  /** The block reading `source`: its default columns, and a range its data covers (see `#fittedRange`). */
  #withSource(block: FiguresBlock | TableBlock, source: string): Partial<Block> & { range: number | null } {
    const kind = block.kind === "figures" ? "stat_tiles" : "table";
    return { source, columns: this.#defaultColumns(source, kind), range: this.#fittedRange(source, block.range) };
  }

  /** Why the block cannot read `source` now, or null. */
  sourceReason(block: FiguresBlock | TableBlock, source: string): Reason {
    return this.#withBlock(block, this.#withSource(block, source));
  }

  /** Reads `source`, taking a shorter range when its data cannot cover the block's, and says so. */
  setSource(block: FiguresBlock | TableBlock, source: string): Reason {
    const reason = this.sourceReason(block, source);
    if (reason) return reason;
    const patch = this.#withSource(block, source);
    const shortened = patch.range !== null && patch.range !== block.range ? patch.range : null;
    this.#update(block, patch);
    if (shortened !== null) this.notice = { key: block.key, text: this.#shorterText(source, shortened) };
    else if (this.notice?.key === block.key) this.notice = null;
    return null;
  }

  /** The block's columns in the catalogue's order, ticked or not. */
  columns(block: FiguresBlock | TableBlock): Column[] {
    const kind = block.kind === "figures" ? "stat_tiles" : "table";
    return this.#defaultColumns(block.source, kind).map((field) => {
      const on = block.columns.includes(field);
      return {
        field,
        label: this.#fieldLabel(block.source, field),
        on,
        reason:
          on && block.columns.length === 1
            ? block.kind === "figures"
              ? "Keep at least one figure."
              : "Keep at least one column."
            : null,
      };
    });
  }

  toggleColumn(block: FiguresBlock | TableBlock, field: string): Reason {
    const column = this.columns(block).find((c) => c.field === field);
    if (!column) return "No such column.";
    if (column.reason) return column.reason;
    const kind = block.kind === "figures" ? "stat_tiles" : "table";
    const order = this.#defaultColumns(block.source, kind);
    const next = column.on
      ? block.columns.filter((c) => c !== field)
      : order.filter((c) => c === field || block.columns.includes(c));
    this.#update(block, { columns: next });
    return null;
  }

  // Comparisons

  get comparisonPresets(): Choice[] {
    const preset: Record<string, unknown> | undefined = this.#dataset(
      this.#vocab.comparisonDataset
    )?.parameters?.properties?.preset;
    const given = preset?.enum;
    const givenLabels = preset?.["x-enumLabels"];
    const values: unknown[] = Array.isArray(given) ? given : [];
    const labels: unknown[] = Array.isArray(givenLabels) ? givenLabels : [];
    return values
      .filter((v): v is string => typeof v === "string")
      .map((value, i) => ({
        value,
        label: String(labels[i] ?? value),
      }));
  }

  setComparisonPreset(block: ComparisonBlock, preset: string) {
    this.#update(block, { preset, a: null, b: null });
  }

  /** Sets the two periods; the comparison runs once both are given. */
  setComparisonPeriods(
    block: ComparisonBlock,
    a: DateSpan | null,
    b: DateSpan | null
  ) {
    this.#update(block, { preset: null, a, b });
  }

  // Words

  blockTitle(block: Block): string {
    if (block.kind === "given") return typeof block.panel.title === "string" ? block.panel.title : "";
    return block.kind === "chart" ? block.ws.title : block.title;
  }

  setBlockTitle(block: Block, title: string) {
    const value = title.slice(0, this.#catalogue.maxTitleLength);
    if (block.kind === "chart") block.ws.title = value;
    else if (block.kind === "given") {
      const panel = copyJson(block.panel);
      if (value.trim()) panel.title = value;
      else delete panel.title;
      this.#update(block, { panel });
    } else this.#update(block, { title: value });
  }

  /** What the block shows, as one sentence. */
  caption(block: Block): string {
    const range = rangeText(this.hoursOf(block), this.#vocab.rangePresets);
    switch (block.kind) {
      case "given":
        return givenCaption(this.#catalogue, block.panel);
      case "chart":
        return block.ws.empty ? "No signals yet" : block.ws.captionText();
      case "figures": {
        const labels = block.columns.map((c) =>
          this.#fieldLabel(block.source, c)
        );
        return `${this.sourceLabel(block)}: ${labels.length} figures ${range}`;
      }
      case "table":
        if (block.source === this.#vocab.eventDataset) {
          const texts = block.events.moments.map((m) =>
            momentText(m, true, this.#clock, block.events.glucoseUnits, this.#vocab)
          );
          return `${upperFirst(texts.join(" against "))}, one row for each event ${range}`;
        }
        return `${this.sourceLabel(block)} ${range}`;
      case "comparison": {
        if (block.preset)
          return (
            this.comparisonPresets.find((p) => p.value === block.preset)
              ?.label ?? block.preset
          );
        if (block.a && block.b)
          return `${this.dates(block.a)} against ${this.dates(block.b)}`;
        return "Choose two periods";
      }
    }
  }

  /** The whole report as one spec: each block's panels, with its own range where it has one. */
  spec(): Record<string, unknown> {
    const panels = this.state.blocks.flatMap((block) => {
      if (block.kind === "given") {
        const panel = copyJson(block.panel);
        return [{ ...panel, id: typeof panel.id === "string" ? panel.id : `b${block.key}-panel` }];
      }
      const own = block.kind === "chart" ? block.ws.ownRange : block.range;
      // A chart's title, which the member typed, heads its first panel: its lag chart follows under
      // it. Small multiples are headed by the engine's name for each group.
      return this.panelsOf(block).map((panel, i) => ({
        ...panel,
        id: block.kind !== "chart" && block.panelId ? block.panelId : `b${block.key}-${String(panel.id)}`,
        ...(panel.group ? { group: `b${block.key}` } : {}),
        ...(block.kind === "chart" && i === 0 && !panel.group && block.ws.title.trim()
          ? { title: block.ws.title.trim() }
          : {}),
        ...(own !== null && block.kind !== "comparison"
          ? { range: { hours: own } }
          : {}),
      }));
    });
    return {
      version: 1,
      ...(this.state.title.trim() ? { title: this.state.title.trim() } : {}),
      ...this.#specRange(),
      panels,
    };
  }

  // Given blocks

  givenLayers(block: GivenBlock): GivenLayer[] {
    return givenLayers(this.#catalogue, block.panel);
  }

  /** Why the given block, its panel changed to `panel`, does not fit the chart's or the report's limits; null when it does. */
  #givenReason(block: GivenBlock, panel: Json): Reason {
    const c = this.#catalogue;
    if (tracksOf(panel).length > c.maxTracks) return `A chart has at most ${c.maxTracks} scales.`;
    if (allLayers(panel).length > c.maxLayers) return `A chart has at most ${c.maxLayers} signals.`;
    return this.#withBlock(block, { panel });
  }

  #applyGiven(block: GivenBlock, panel: Json): Reason {
    const reason = this.#givenReason(block, panel);
    if (!reason) this.#update(block, { panel });
    return reason;
  }

  renameGivenLayer(block: GivenBlock, layer: GivenLayer, title: string) {
    this.#update(block, { panel: renameLayer(block.panel, layer.track, layer.index, title.slice(0, this.#catalogue.maxTitleLength)) });
  }

  setGivenMark(block: GivenBlock, layer: GivenLayer, mark: string): Reason {
    if (!layer.marks.includes(mark)) return "It cannot be drawn that way.";
    return this.#applyGiven(block, setLayerMark(this.#catalogue, block.panel, layer.track, layer.index, mark));
  }

  ownScaleReason(block: GivenBlock, layer: GivenLayer, on: boolean): Reason {
    return ownScaleReason(this.#catalogue, block.panel, layer.track, layer.index, on, this.#catalogue.maxTracks);
  }

  setGivenOwnScale(block: GivenBlock, layer: GivenLayer, on: boolean): Reason {
    const reason = this.ownScaleReason(block, layer, on);
    return reason ?? this.#applyGiven(block, setOwnScale(block.panel, layer.track, layer.index, on));
  }

  removeGivenLayerReason(block: GivenBlock): Reason {
    return removeLayerReason(block.panel);
  }

  removeGivenLayer(block: GivenBlock, layer: GivenLayer): Reason {
    return this.removeGivenLayerReason(block) ?? this.#applyGiven(block, removeLayer(block.panel, layer.track, layer.index));
  }

  setGivenLayerRange(block: GivenBlock, layer: GivenLayer, hours: number | null): Reason {
    return this.#applyGiven(block, setLayerRange(block.panel, layer.track, layer.index, hours));
  }

  /** Why a calculated signal of `op` cannot be added now, or null. */
  calculatedReason(block: GivenBlock, op: CalculatedOp, a: GivenSource | null, b: GivenSource | null, minutes: number | null): Reason {
    if (!a) return "Choose the signal to calculate from first.";
    if ((op === "difference" || op === "ratio" || op === "scatter" || op === "lag_correlation") && !b)
      return "Choose the second signal first.";
    return this.#givenReason(block, addCalculated(this.#catalogue, block.panel, op, a, b, minutes));
  }

  addGivenCalculated(block: GivenBlock, op: CalculatedOp, a: GivenSource | null, b: GivenSource | null, minutes: number | null): Reason {
    const reason = this.calculatedReason(block, op, a, b, minutes);
    if (reason || !a) return reason;
    this.#update(block, { panel: addCalculated(this.#catalogue, block.panel, op, a, b, minutes) });
    return null;
  }

  /** The lens whose signals a given chart can take: the one that draws along its first row's axis. */
  givenLens(block: GivenBlock): LensId {
    const first = allLayers(block.panel)[0];
    return lensForAxis(first ? axisOf(this.#catalogue, first) : null);
  }

  /** Why a signal of the catalogue cannot be added to a given chart now, or null. */
  givenSignalReason(block: GivenBlock, id: SignalId): Reason {
    const cell = this.#vocab.cellOf(this.givenLens(block), id);
    if (!cell) return "This chart cannot show it.";
    if (!cell.permitted) return accessReason(cell.scopes);
    return this.#givenReason(block, addSignalLayer(block.panel, id, cell));
  }

  addGivenSignal(block: GivenBlock, id: SignalId): Reason {
    const reason = this.givenSignalReason(block, id);
    const cell = this.#vocab.cellOf(this.givenLens(block), id);
    if (reason || !cell) return reason;
    this.#update(block, { panel: addSignalLayer(block.panel, id, cell) });
    return null;
  }

  givenColumns(block: GivenBlock) {
    const columns = givenColumns(this.#catalogue, block.panel);
    const on = columns.filter((c) => c.on).length;
    return columns.map((c) => ({
      ...c,
      reason: c.on && on === 1 ? (block.panel.kind === "stat_tiles" ? "Keep at least one figure." : "Keep at least one column.") : null,
    }));
  }

  toggleGivenColumn(block: GivenBlock, field: string): Reason {
    const column = this.givenColumns(block).find((c) => c.field === field);
    if (!column) return "No such column.";
    if (column.reason) return column.reason;
    this.#update(block, { panel: toggleGivenColumn(this.#catalogue, block.panel, field) });
    return null;
  }

  /** A given comparison's periods: its preset, or the two periods it names. */
  givenComparison(block: GivenBlock): { preset: string | null; a: DateSpan | null; b: DateSpan | null } {
    const dataset = asObject(block.panel.dataset) ?? {};
    const span = (v: unknown): DateSpan | null => {
      const o = asObject(v);
      return o && typeof o.from === "string" && typeof o.to === "string" ? { from: o.from, to: o.to } : null;
    };
    return { preset: typeof dataset.preset === "string" ? dataset.preset : null, a: span(dataset.a), b: span(dataset.b) };
  }

  /** Sets a given comparison to a preset pair of periods, or to two periods of the member's own. */
  setGivenComparison(block: GivenBlock, periods: { preset: string } | { a: DateSpan | null; b: DateSpan | null }) {
    const panel = copyJson(block.panel);
    const dataset = asObject(panel.dataset) ?? {};
    panel.dataset =
      "preset" in periods
        ? { ...dataset, preset: periods.preset, a: null, b: null }
        : { ...dataset, preset: null, a: periods.a, b: periods.b };
    this.#update(block, { panel });
  }

  /**
   * Replaces a chart of signals with the panels it makes, each held as given, so each of its signals
   * can be changed on its own. Its view and groups are then fixed as they are.
   */
  changeSignalBySignal(block: ChartBlock): Reason {
    const at = this.state.blocks.findIndex((b) => b.key === block.key);
    if (at < 0 || block.ws.built.panels.length === 0) return "Add a signal first.";
    const own = block.ws.ownRange;
    const panels = block.ws.built.panels.map((panel, i): Json => ({
      ...copyJson(panel),
      id: `b${block.key}-${String(panel.id)}`,
      ...(panel.group ? { group: `b${block.key}` } : {}),
      ...(i === 0 && !panel.group && block.ws.title.trim() ? { title: block.ws.title.trim() } : {}),
      ...(own !== null ? { range: { hours: own } } : {}),
    }));
    const blocks: GivenBlock[] = panels.map((panel) => ({ kind: "given", key: freshKey(), range: null, panel }));
    this.state.blocks.splice(at, 1, ...blocks);
    this.state.open = blocks[0].key;
    return null;
  }

  /** Why the report cannot be saved now, or null. */
  get saveReason(): Reason {
    if (this.state.blocks.every((b) => this.panelsOf(b).length === 0))
      return "Add a signal or a block first.";
    const at = this.state.blocks.findIndex((b) => this.incomplete(b) !== null);
    if (at >= 0) return `Finish or remove block ${at + 1} first. ${this.incomplete(this.state.blocks[at])}`;
    return null;
  }

  /** What the member does next before the block can run, or null. */
  incomplete(block: Block): string | null {
    if (block.kind === "chart") return block.ws.incomplete;
    const periods = block.kind === "comparison" ? block : block.kind === "given" && block.panel.kind === "comparison" ? this.givenComparison(block) : null;
    if (periods && !periods.preset && !(periods.a && periods.b)) return "Pick both periods.";
    return null;
  }
}
