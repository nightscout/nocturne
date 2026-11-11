/**
 * The workspace's vocabulary as the API's report catalogue states it (`catalogue.workspace`): lenses,
 * signals and how each lens draws them, kinds of event and day, presets, the views of the events and
 * the sources of blocks. All by id; the words for them are the web's (`vocabulary-words.svelte.ts`).
 * A plain module, so no id here is translated on its way to a spec.
 */
import type {
  CustomReportCatalogue,
  CustomReportLayerBindings,
  CustomReportWorkspace,
  CustomReportWorkspaceAnchor,
  CustomReportWorkspaceCell,
  CustomReportWorkspaceMark,
  CustomReportWorkspaceSignal,
} from "#lib/api/generated/nocturne-api-client.js";

export type SignalId = CustomReportWorkspaceSignal["id"];
export type LensId = CustomReportWorkspace["lenses"][number];
export type Family = CustomReportWorkspaceSignal["family"];
export type AnchorId = CustomReportWorkspaceAnchor["id"];
export type AnchorGroup = CustomReportWorkspaceAnchor["group"];
export type Weekday = CustomReportWorkspace["dayGroupPresets"][number]["groups"][number][number];
export type DayKind = CustomReportWorkspace["dayKinds"][number]["id"];
export type DayGroupPresetId = CustomReportWorkspace["dayGroupPresets"][number]["id"];
export type LeaveOutId = CustomReportWorkspace["leaveOuts"][number]["id"];
export type RangePresetId = CustomReportWorkspace["rangePresets"][number]["id"];
export type EventShow = CustomReportWorkspace["events"]["shows"][number]["id"];
export type Outcome = CustomReportWorkspace["events"]["outcomes"][number]["id"];
export type Along = CustomReportWorkspace["events"]["along"][number];
export type WhenAxis = CustomReportWorkspace["events"]["whenAxes"][number];
export type CombineOp = CustomReportWorkspace["combineOps"][number];
export type Cell = CustomReportWorkspaceCell;
export type MarkChoice = CustomReportWorkspaceMark;

/** The view of the events that draws signals over time; the others draw the events one by one. */
export const CURVE: EventShow = "curve";

/** The binding slots a mark sets, without the ones the catalogue writes as null. */
export function bindingsOf(mark: MarkChoice): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(mark.bindings satisfies CustomReportLayerBindings))
    if (value !== null && value !== undefined) out[key] = value;
  return out;
}

function schemaAt(value: unknown, ...path: string[]): unknown {
  let at = value;
  for (const key of path) at = at && typeof at === "object" ? Reflect.get(at, key) : undefined;
  return at;
}

function numbers(value: unknown): number[] {
  return Array.isArray(value) ? value.filter((v): v is number => typeof v === "number") : [];
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
}

/** A catalogue's workspace vocabulary, indexed for the model. */
export class Vocabulary {
  readonly catalogue: CustomReportCatalogue;
  readonly ws: CustomReportWorkspace;
  readonly #cells = new Map<string, Cell>();

  constructor(catalogue: CustomReportCatalogue) {
    this.catalogue = catalogue;
    this.ws = catalogue.workspace;
    for (const cell of this.ws.cells) this.#cells.set(`${cell.lens}\u0000${cell.signal}`, cell);
  }

  get lenses(): LensId[] {
    return this.ws.lenses;
  }

  get signals(): CustomReportWorkspaceSignal[] {
    return this.ws.signals;
  }

  signal(id: SignalId): CustomReportWorkspaceSignal | undefined {
    return this.ws.signals.find((s) => s.id === id);
  }

  /** The CSS colour of a signal's theme token. */
  signalColor(id: SignalId): string {
    const color = this.signal(id)?.color;
    return color ? `var(--${color})` : "var(--muted-foreground)";
  }

  /** The lane a signal is drawn in: its own lane's family, else its family. */
  laneOf(id: SignalId): Family {
    const s = this.signal(id);
    return s?.lane ?? s?.family ?? "";
  }

  cellOf(lens: LensId, id: SignalId): Cell | undefined {
    return this.#cells.get(`${lens}\u0000${id}`);
  }

  /** The lenses that draw the signal, in lens order. */
  lensesOf(id: SignalId): LensId[] {
    return this.ws.lenses.filter((l) => this.cellOf(l, id) !== undefined);
  }

  get families(): Family[] {
    return this.ws.families.map((f) => f.id);
  }

  laneHeight(family: Family): number {
    return this.ws.families.find((f) => f.id === family)?.laneHeight ?? 1;
  }

  /** Whether a mark takes a value axis, by the catalogue's marks; markers and spans do not. */
  hasAxis(mark: string): boolean {
    return this.catalogue.marks.find((m) => m.name === mark)?.hasAxis ?? true;
  }

  get anchors(): CustomReportWorkspaceAnchor[] {
    return this.ws.anchors;
  }

  get anchorGroups(): AnchorGroup[] {
    return this.ws.anchorGroups;
  }

  anchor(id: AnchorId): CustomReportWorkspaceAnchor | undefined {
    return this.ws.anchors.find((a) => a.id === id);
  }

  /** Whether events of the kind can be narrowed by meal size and food. */
  mealNarrowing(id: AnchorId): boolean {
    return this.anchor(id)?.mealNarrowing ?? false;
  }

  /** The kind of event a new group starts as: the catalogue's first. */
  get firstAnchor(): AnchorId {
    return this.ws.anchors[0]?.id ?? "";
  }

  get dayKinds() {
    return this.ws.dayKinds;
  }

  dayGroupPreset(id: DayGroupPresetId): Weekday[][] {
    return (this.ws.dayGroupPresets.find((p) => p.id === id)?.groups ?? []).map((g) => [...g]);
  }

  get dayGroupPresets() {
    return this.ws.dayGroupPresets;
  }

  get leaveOuts() {
    return this.ws.leaveOuts;
  }

  get rangePresets() {
    return this.ws.rangePresets;
  }

  get defaultReportHours(): number {
    return this.ws.defaultReportHours;
  }

  get combineOps(): CombineOp[] {
    return this.ws.combineOps;
  }

  /** The marks a combination can be drawn as: its op's, by the catalogue's ops. */
  comboMarks(op: CombineOp): string[] {
    return this.catalogue.ops.find((o) => o.name === op)?.marks.map((m) => m.name) ?? [];
  }

  get againstGridMinutes(): number {
    return this.ws.againstGridMinutes;
  }

  /** The grid a smoothed timeline of `rangeHours` is drawn on: the finest that keeps the axis within its slots. */
  timelineGrid(rangeHours: number): number {
    const least = Math.ceil((rangeHours * 60) / this.ws.maxTimeSlots);
    const grids = this.ws.timelineGrids;
    return grids.find((g) => g >= least) ?? grids[grids.length - 1] ?? least;
  }

  get shows(): EventShow[] {
    return this.ws.events.shows.map((s) => s.id);
  }

  showDataset(show: EventShow): string {
    return this.ws.events.shows.find((s) => s.id === show)?.dataset ?? "";
  }

  /** The dataset of the curve over time around events: the one whose schema holds the groups and windows. */
  get eventDataset(): string {
    return this.showDataset(CURVE);
  }

  get outcomes() {
    return this.ws.events.outcomes;
  }

  get alongs(): Along[] {
    return this.ws.events.along;
  }

  /** Whether a line can go through the points along an axis. */
  lineAlong(along: Along): boolean {
    return !this.ws.events.noLineAlong.includes(along);
  }

  get whenAxes(): WhenAxis[] {
    return this.ws.events.whenAxes;
  }

  get figureSources(): string[] {
    return this.ws.sources.figures;
  }

  get tableSources(): string[] {
    return this.ws.sources.tables;
  }

  get comparisonDataset(): string {
    return this.ws.sources.comparison;
  }

  #eventProperty(...path: string[]): unknown {
    const params: unknown = this.catalogue.datasets.find((d) => d.name === this.eventDataset)?.parameters;
    return schemaAt(params, "properties", ...path);
  }

  /** The weekdays, in order: the event dataset's `dayOfWeek` enum. */
  get weekdays(): Weekday[] {
    return strings(this.#eventProperty("cohorts", "items", "properties", "dayOfWeek", "items", "enum"));
  }

  /** The windows before and after an event the engine takes, and its defaults. */
  get windows(): { before: number[]; after: number[]; defaultBefore: number; defaultAfter: number } {
    const before = numbers(this.#eventProperty("minutesBefore", "enum"));
    const after = numbers(this.#eventProperty("minutesAfter", "enum"));
    const def = (name: string, choices: number[]) => {
      const given = this.#eventProperty(name, "default");
      return typeof given === "number" ? given : (choices[0] ?? 0);
    };
    return { before, after, defaultBefore: def("minutesBefore", before), defaultAfter: def("minutesAfter", after) };
  }
}
