/**
 * The workspace's own state, saved with a report in the spec's `builder` member, so the report
 * reopens with its signals, lens and groups. The spec does not hold them: it is what they produce.
 *
 * A plain module, not `.svelte.ts`: the kinds, ids and keys here are read back, and wuchale would
 * translate a capitalised literal in a `.svelte.ts` file.
 */
import { z } from "zod";
import {
  ALONGS,
  ANCHORS,
  DAY_KINDS,
  EVENT_SHOWS,
  FAMILY_LANES,
  LENSES,
  OUTCOMES,
  SIGNALS,
  WEEKDAY_IDS,
} from "./vocabulary.js";
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

/** One of the given ids. */
function oneOf<T extends string>(ids: readonly T[]) {
  return z.custom<T>((v) => ids.some((id) => id === v));
}

const lensId = oneOf(LENSES.map((l) => l.id));
const signalId = oneOf(SIGNALS.map((s) => s.id));
const anchorId = oneOf(ANCHORS.map((a) => a.id));
const weekday = oneOf(WEEKDAY_IDS);
const outcome = oneOf(OUTCOMES.map((o) => o.id));
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
      family: oneOf(FAMILY_LANES.map((f) => f.family)),
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
      op: z.enum(["difference", "ratio"]),
      mark: z.string().optional(),
    })
  ),
  moments: z.array(moment).min(1),
  dayGroups: z.array(z.array(weekday).min(1)).min(1),
  dayFilter: z
    .strictObject({
      kind: oneOf(DAY_KINDS.map((k) => k.id)),
      minCarbs: z.number().optional(),
      maxCarbs: z.number().optional(),
    })
    .nullable(),
  findLag: z.boolean(),
  leaveOut: z.strictObject({
    overrides: z.boolean(),
    temporaryTargets: z.boolean(),
    exercise: z.boolean(),
    illness: z.boolean(),
    openLoop: z.boolean(),
  }),
  profileName: z.string().nullable(),
  event: z.strictObject({
    before: minute,
    after: minute,
    isolatedOnly: z.boolean(),
    requireCorroboration: z.boolean(),
    comparison: span.nullable(),
    show: oneOf(EVENT_SHOWS.map((s) => s.id)),
    up: outcome,
    along: oneOf(ALONGS.map((a) => a.id)),
    fit: z.boolean(),
    figure: outcome,
    when: z.enum(["hour", "weekday"]),
  }),
}) satisfies z.ZodType<WorkspaceState>;

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

const savedReport = z.strictObject({
  title: z.string(),
  rangeHours: z.number().int().positive(),
  blocks: z.array(savedBlock).min(1),
});

/** A report as the workspace saves it: its title, range and blocks, with the keys its panel ids carry. */
export type SavedReport = z.infer<typeof savedReport>;
export type SavedBlock = z.infer<typeof savedBlock>;

/**
 * The saved state, when it is of `version` and of that version's shape; else null, so a state of
 * another shape is refused, never read as if it were this one.
 */
export function parseSavedReport(version: unknown, state: unknown): SavedReport | null {
  if (version !== BUILDER_VERSION) return null;
  const parsed = savedReport.safeParse(state);
  return parsed.success ? parsed.data : null;
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
