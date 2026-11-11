import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import {
  COMPARISON_DATASET,
  DEFAULT_REPORT_HOURS,
  EVENT_DATASET,
  FIGURE_SOURCES,
  RANGE_PRESETS,
  TABLE_SOURCES,
  type BlockKind,
  type Source,
} from "./vocabulary.js";
import { rangeLabel, sourceLabel, starterWords } from "./vocabulary-words.svelte.js";
import { parseSavedReport, type SavedBlock, type SavedReport } from "./saved-state.js";
import {
  Workspace,
  eventParameters,
  freshKey,
  reserveKey,
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
}

export interface TableBlock extends BlockBase {
  kind: "table";
  title: string;
  source: string;
  columns: string[];
  /** The groups of events of an "each event" table, edited as a chart's moments are. */
  events: Workspace;
}

export interface ComparisonBlock extends BlockBase {
  kind: "comparison";
  title: string;
  /** A preset pair of periods, or null for the two periods given. */
  preset: string | null;
  a: DateSpan | null;
  b: DateSpan | null;
}

export type Block = ChartBlock | FiguresBlock | TableBlock | ComparisonBlock;

export interface ReportState {
  title: string;
  rangeHours: number;
  blocks: Block[];
  /** The key of the block open for editing, or null for none. */
  open: number | null;
}

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
    rangeHours: DEFAULT_REPORT_HOURS,
    blocks: [],
    open: null,
  });

  readonly #catalogue: CustomReportCatalogue;
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
    this.#clock = clock ?? ((m) => String(m));
    this.dates = dates ?? ((span) => `${span.from} to ${span.to}`);
    this.state = {
      title: starter ? (starterWords(this.#clock)[starter.id]?.title ?? "") : "",
      rangeHours: starter?.rangeHours ?? DEFAULT_REPORT_HOURS,
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
          return { kind: "figures", key: block.key, range: block.range, title: block.title, source: block.source, columns: [...block.columns] };
        case "table":
          return {
            kind: "table",
            key: block.key,
            range: block.range,
            title: block.title,
            source: block.source,
            columns: [...block.columns],
            ...(block.source === EVENT_DATASET ? { events: $state.snapshot(block.events.state) } : {}),
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
          };
      }
    });
    // A JSON round trip drops the keys an optional narrowing leaves undefined, as the saved form has them.
    return JSON.parse(JSON.stringify({ title: this.state.title, rangeHours: this.state.rangeHours, blocks }));
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
    const saved = parseSavedReport(version, state);
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
      ...saved.blocks.map((b) => (b.kind === "chart" ? b.chart.title : b.title)),
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
          };
        case "comparison":
          return { ...b };
      }
    });
    this.state = { title: saved.title, rangeHours: saved.rangeHours, blocks, open: blocks[0]?.key ?? null };
  }

  /** The range a block reads, its own or the report's. */
  hoursOf(block: Block): number {
    if (block.kind === "chart") return block.ws.rangeHours;
    return block.range ?? this.state.rangeHours;
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
              ...(block.source === EVENT_DATASET
                ? eventParameters(
                    block.events.state,
                    block.events.leaveOutDefaults
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
              name: COMPARISON_DATASET,
              ...(block.preset
                ? { preset: block.preset }
                : block.a && block.b
                  ? { a: block.a, b: block.b }
                  : {}),
            },
          },
        ];
    }
  }

  /** The spec a block's preview runs, or null while it draws nothing. */
  previewSpec(block: Block): Record<string, unknown> | null {
    const panels = this.panelsOf(block);
    if (panels.length === 0) return null;
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
    const maxComparisons = this.#dataset(COMPARISON_DATASET)?.maxReadsPerReport;
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

  #newBlock(kind: BlockKind): Block {
    const key = freshKey();
    switch (kind) {
      case "chart":
        return this.#chart();
      case "figures": {
        const source = this.figureSources[0]?.dataset ?? FIGURE_SOURCES[0].dataset;
        return {
          kind,
          key,
          range: null,
          title: "",
          source,
          columns: this.#defaultColumns(source, "stat_tiles"),
        };
      }
      case "table": {
        const source = this.tableSources[0]?.dataset ?? TABLE_SOURCES[0].dataset;
        return {
          kind,
          key,
          range: null,
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
    switch (kind) {
      case "chart":
        return { panels: 1, reads: [], comparison: false };
      case "figures":
        return {
          panels: 1,
          reads: [
            `stat_tiles|${hours}|${JSON.stringify({ name: this.figureSources[0]?.dataset })}`,
          ],
          comparison: false,
        };
      case "table":
        return {
          panels: 1,
          reads: [
            `table|${hours}|${JSON.stringify({ name: this.tableSources[0]?.dataset })}`,
          ],
          comparison: false,
        };
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
      !this.#dataset(COMPARISON_DATASET)?.permitted
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
    const dataset = this.#dataset(block.source);
    if (dataset && hours > dataset.maxRangeHours)
      return `${this.sourceLabel(block)} shows at most ${Math.floor(dataset.maxRangeHours / 24)} days. Choose a shorter range first.`;
    return null;
  }

  /** The report's range presets, each with why it cannot be chosen now. */
  get ranges(): RangeOption[] {
    return RANGE_PRESETS.map((p) => ({ ...p, label: rangeLabel(p.id), reason: this.rangeReason(p.hours) }));
  }

  rangeReason(hours: number): Reason {
    for (const block of this.state.blocks) {
      if (block.range !== null && block.kind !== "chart") continue;
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
    if (!reason) this.state.rangeHours = hours;
    return reason;
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
        : RANGE_PRESETS.map((p) => ({
            ...p,
            reason: this.#withBlock(block, { range: p.hours }),
          }));
    return [
      report,
      ...presets.map((p) => ({
        id: p.id,
        label: rangeLabel(p.id),
        hours: p.hours,
        reason: p.reason,
      })),
    ];
  }

  /** The id of a block's range choice. */
  blockRangeId(block: Block): string {
    const own = block.kind === "chart" ? block.ws.ownRange : block.range;
    if (own === null) return SAME_AS_REPORT;
    return RANGE_PRESETS.find((p) => p.hours === own)?.id ?? "";
  }

  setBlockRange(block: Block, hours: number | null): Reason {
    if (block.kind === "chart") return block.ws.setRange(hours);
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

  #permittedSources(list: readonly Source[], kind: string): Source[] {
    return list.filter((s) => {
      const d = this.#dataset(s.dataset);
      return d?.permitted && d.panelKinds.some((k) => k.name === kind);
    });
  }

  get figureSources(): Source[] {
    return this.#permittedSources(FIGURE_SOURCES, "stat_tiles");
  }

  get tableSources(): Source[] {
    return this.#permittedSources(TABLE_SOURCES, "table");
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
  sourceReason(block: FiguresBlock | TableBlock, source: string): Reason {
    const kind = block.kind === "figures" ? "stat_tiles" : "table";
    return this.#withBlock(block, {
      source,
      columns: this.#defaultColumns(source, kind),
    });
  }

  setSource(block: FiguresBlock | TableBlock, source: string): Reason {
    const reason = this.sourceReason(block, source);
    if (reason) return reason;
    const kind = block.kind === "figures" ? "stat_tiles" : "table";
    this.#update(block, { source, columns: this.#defaultColumns(source, kind) });
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
      COMPARISON_DATASET
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
    return block.kind === "chart" ? block.ws.title : block.title;
  }

  setBlockTitle(block: Block, title: string) {
    const value = title.slice(0, this.#catalogue.maxTitleLength);
    if (block.kind === "chart") block.ws.title = value;
    else this.#update(block, { title: value });
  }

  /** What the block shows, as one sentence. */
  caption(block: Block): string {
    const range = rangeText(this.hoursOf(block));
    switch (block.kind) {
      case "chart":
        return block.ws.empty ? "No signals yet" : block.ws.captionText();
      case "figures": {
        const labels = block.columns.map((c) =>
          this.#fieldLabel(block.source, c)
        );
        return `${this.sourceLabel(block)}: ${labels.length} figures ${range}`;
      }
      case "table":
        if (block.source === EVENT_DATASET) {
          const texts = block.events.moments.map((m) =>
            momentText(m, true, this.#clock, block.events.glucoseUnits)
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
      const own = block.kind === "chart" ? block.ws.ownRange : block.range;
      // A chart's title, which the member typed, heads its first panel: its small multiples and lag
      // chart follow under it.
      return this.panelsOf(block).map((panel, i) => ({
        ...panel,
        id: `b${block.key}-${String(panel.id)}`,
        ...(block.kind === "chart" && i === 0 && block.ws.title.trim()
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
      range: { hours: this.state.rangeHours },
      panels,
    };
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
    if (block.kind === "comparison" && !block.preset && !(block.a && block.b)) return "Pick both periods.";
    return null;
  }
}
