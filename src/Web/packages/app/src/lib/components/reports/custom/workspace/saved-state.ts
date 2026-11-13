/**
 * The workspace's own state, saved with a report in the spec's `builder` member, so the report
 * reopens with its signals, lens and groups. The spec does not hold them: it is what they produce.
 *
 * A plain module, not `.svelte.ts`: the kinds, ids and keys here are read back, and wuchale would
 * translate a capitalised literal in a `.svelte.ts` file.
 */
import { z } from "zod";
import type { Vocabulary } from "./catalogue-vocabulary.js";
import type { WorkspaceState } from "./workspace.svelte.js";

/** The `builder.kind` of a report made in the workspace. */
export const BUILDER_KIND = "workspace";

/**
 * The version of the saved state's shape. Raise it on any change a state of the old shape would be
 * misread under; `parseSavedReport` refuses every other version, and such a report opens in the form.
 */
export const BUILDER_VERSION = 1;

/** The most of the `builder` member, as UTF-8 JSON, the engine keeps. */
export const BUILDER_MAX_BYTES = 32 * 1024;

// Ids are strings here; `knownIds` checks each against the catalogue's vocabulary.
const lensId = z.string();
const signalId = z.string();
const anchorId = z.string();
const weekday = z.string();
const outcome = z.string();
const minute = z.number().int().min(0).max(1440);
const key = z.number().int().nonnegative();
const date = z.string().regex(/^\d{4}-\d{2}-\d{2}$/);
const span = z.strictObject({ from: date, to: date });
const bounds = z.strictObject({ min: z.number().optional(), max: z.number().optional() });

const relate = z.strictObject({
  smooth: z.number().int().positive().nullable(),
  sd: z.number().int().positive().nullable(),
  trend: z.boolean(),
  confidence: z.boolean(),
  fromStart: z.boolean(),
});

const moment = z.strictObject({
  key,
  what: anchorId,
  timeOfDay: z.strictObject({ fromMinute: minute, toMinute: minute }).optional(),
  weekdays: z.array(weekday).optional(),
  grams: bounds.optional(),
  foods: z.array(z.strictObject({ id: z.string(), name: z.string() })).optional(),
  foodCategory: z.string().optional(),
  minutesSinceMeal: bounds.optional(),
  startGlucose: bounds.optional(),
  insulinOnBoard: bounds.optional(),
  everythingElse: z.boolean().optional(),
});

const workspaceState = z.strictObject({
  title: z.string(),
  range: z.number().int().positive().nullable(),
  lens: lensId,
  placed: z.array(
    z.strictObject({
      id: signalId,
      marks: z.partialRecord(lensId, z.string()),
      relate: z.partialRecord(lensId, relate),
    })
  ),
  lanes: z.array(
    z.strictObject({
      id: z.string(),
      family: z.string(),
      own: z.boolean(),
      signals: z.array(signalId),
    })
  ),
  combos: z.array(
    z.strictObject({
      key,
      lens: lensId,
      a: signalId,
      b: signalId,
      op: z.string(),
      mark: z.string().optional(),
    })
  ),
  moments: z.array(moment).min(1),
  dayGroups: z.array(z.array(weekday).min(1)).min(1),
  dayFilter: z
    .strictObject({
      kind: z.string(),
      minCarbs: z.number().optional(),
      maxCarbs: z.number().optional(),
    })
    .nullable(),
  findLag: z.boolean(),
  leaveOut: z.record(z.string(), z.boolean()),
  profileName: z.string().max(100).nullable(),
  event: z.strictObject({
    before: minute,
    after: minute,
    isolatedOnly: z.boolean(),
    requireCorroboration: z.boolean(),
    comparison: span.nullable(),
    show: z.string(),
    up: outcome,
    along: z.string(),
    fit: z.boolean(),
    figure: outcome,
    when: z.string(),
  }),
})
  .superRefine((state, ctx) => {
    const placed = state.placed.map((p) => p.id);
    const inLanes = state.lanes.flatMap((l) => l.signals);
    const problems = [
      hasDuplicates(placed) && "a signal placed twice",
      inLanes.some((id) => !placed.includes(id)) && "a lane names a signal that is not placed",
      hasDuplicates(inLanes) && "a signal in two lanes",
      hasDuplicates(state.lanes.map((l) => l.id)) && "two lanes of one id",
      state.combos.some((c) => !placed.includes(c.a) || !placed.includes(c.b)) && "a combination of a signal that is not placed",
      hasDuplicates(state.combos.map((c) => c.key)) && "two combinations of one key",
      hasDuplicates(state.moments.map((m) => m.key)) && "two groups of one key",
    ].filter((p): p is string => typeof p === "string");
    for (const message of problems) ctx.addIssue({ code: "custom", message });
  }) satisfies z.ZodType<WorkspaceState>;

function hasDuplicates(values: readonly unknown[]): boolean {
  return new Set(values).size !== values.length;
}

const blockRange = z.number().int().positive().nullable();
const columns = z.array(z.string()).min(1);

const savedBlock = z.discriminatedUnion("kind", [
  z.strictObject({ kind: z.literal("chart"), key, chart: workspaceState }),
  z.strictObject({ kind: z.literal("figures"), key, range: blockRange, title: z.string(), source: z.string(), columns }),
  z.strictObject({
    kind: z.literal("table"),
    key,
    range: blockRange,
    title: z.string(),
    source: z.string(),
    columns,
    /** The groups of events of an "each event" table; absent for any other source. */
    events: workspaceState.optional(),
  }),
  z.strictObject({
    kind: z.literal("comparison"),
    key,
    range: blockRange,
    title: z.string(),
    preset: z.string().nullable(),
    a: span.nullable(),
    b: span.nullable(),
  }),
]);

const savedReport = z
  .strictObject({
    title: z.string(),
    rangeHours: z.number().int().positive(),
    blocks: z.array(savedBlock).min(1),
  })
  .superRefine((report, ctx) => {
    const keys = report.blocks.map((b) => b.key);
    if (hasDuplicates(keys)) ctx.addIssue({ code: "custom", message: "two blocks of one key" });
  });

/** A report as the workspace saves it: its title, range and blocks, with the keys its panel ids carry. */
export type SavedReport = z.infer<typeof savedReport>;
export type SavedBlock = z.infer<typeof savedBlock>;

/**
 * The saved state, when it is of `version` and of that version's shape; else null, so a state of
 * another shape is refused, never read as if it were this one.
 */
export function parseSavedReport(version: unknown, state: unknown, vocab: Vocabulary): SavedReport | null {
  if (version !== BUILDER_VERSION) return null;
  const parsed = savedReport.safeParse(state);
  return parsed.success && knownIds(parsed.data, vocab) ? parsed.data : null;
}

/** Whether every id a saved state names is one the catalogue's vocabulary has. */
function knownIds(saved: SavedReport, vocab: Vocabulary): boolean {
  const states = saved.blocks.flatMap((b) => (b.kind === "chart" ? [b.chart] : b.kind === "table" && b.events ? [b.events] : []));
  const lenses = new Set(vocab.lenses);
  const signals = new Set(vocab.signals.map((s) => s.id));
  const anchors = new Set(vocab.anchors.map((a) => a.id));
  const weekdays = new Set(vocab.weekdays);
  const families = new Set(vocab.families);
  const outcomes = new Set(vocab.outcomes.map((o) => o.id));
  const leaveOuts = new Set(vocab.leaveOuts.map((l) => l.id));
  return states.every(
    (s) =>
      lenses.has(s.lens) &&
      s.placed.every((p) => signals.has(p.id) && Object.keys(p.marks).every((l) => lenses.has(l)) && Object.keys(p.relate).every((l) => lenses.has(l))) &&
      s.lanes.every((l) => families.has(l.family)) &&
      s.combos.every((c) => lenses.has(c.lens) && vocab.combineOps.includes(c.op)) &&
      s.moments.every((m) => anchors.has(m.what) && (m.weekdays ?? []).every((d) => weekdays.has(d))) &&
      s.dayGroups.every((g) => g.every((d) => weekdays.has(d))) &&
      (s.dayFilter === null || vocab.dayKinds.some((k) => k.id === s.dayFilter?.kind)) &&
      Object.keys(s.leaveOut).every((k) => leaveOuts.has(k)) &&
      vocab.shows.includes(s.event.show) &&
      outcomes.has(s.event.up) &&
      outcomes.has(s.event.figure) &&
      vocab.alongs.includes(s.event.along) &&
      vocab.whenAxes.includes(s.event.when)
  );
}

/** The `builder` member a report made in the workspace carries in its spec. */
export interface BuilderMember {
  kind: string;
  version: number;
  state: unknown;
}

/** The spec with the workspace's state as its `builder` member. */
export function withBuilder(spec: Record<string, unknown>, state: SavedReport): Record<string, unknown> {
  return { ...spec, builder: { kind: BUILDER_KIND, version: BUILDER_VERSION, state } satisfies BuilderMember };
}

/** The spec without its `builder` member: what the engine reads. */
export function withoutBuilder(spec: unknown): Record<string, unknown> {
  if (!spec || typeof spec !== "object" || Array.isArray(spec)) return {};
  const { builder: _builder, ...rest } = Object.fromEntries(Object.entries(spec));
  return rest;
}

/** The workspace's `builder` member of a spec, or null for none or another builder's. */
export function builderOf(spec: unknown): BuilderMember | null {
  if (!spec || typeof spec !== "object" || !("builder" in spec)) return null;
  const member: unknown = spec.builder;
  if (!member || typeof member !== "object") return null;
  const kind: unknown = Reflect.get(member, "kind");
  const version: unknown = Reflect.get(member, "version");
  if (kind !== BUILDER_KIND || typeof version !== "number") return null;
  return { kind, version, state: Reflect.get(member, "state") };
}

/**
 * Whether two JSON values are the same: key order ignored (the store reorders keys), numbers compared
 * as numbers, array order kept.
 */
export function sameJson(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
    return a.every((item, i) => sameJson(item, b[i]));
  }
  if (!a || !b || typeof a !== "object" || typeof b !== "object") return false;
  const ka = Object.keys(a);
  const kb = Object.keys(b);
  if (ka.length !== kb.length) return false;
  return ka.every((k) => k in b && sameJson(Reflect.get(a, k), Reflect.get(b, k)));
}
