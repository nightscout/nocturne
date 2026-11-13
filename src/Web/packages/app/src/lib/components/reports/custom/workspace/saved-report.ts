/**
 * Opening and saving a report made in the workspace. The spec carries the workspace's state in its
 * `builder` member (see saved-state.ts); a saved report reopens in the workspace only when that
 * state still produces the spec, so a report changed anywhere else opens in the form.
 */
import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import { Report } from "./report.svelte.js";
import type { DateSpan } from "./workspace.svelte.js";
import { BUILDER_MAX_BYTES, builderOf, sameJson, withBuilder, withoutBuilder } from "./saved-state.js";

/**
 * Which builder a saved report opens in. The form says why: the report carries no state of this
 * builder (or one of another version), or its state no longer makes the spec saved.
 */
export type Opening =
  | { builder: "workspace"; report: Report }
  | { builder: "form"; reason: "notMade" | "changed" };

/**
 * The spec as the engine stores it (version stamped, ids assigned, defaults written), or null when it
 * does not validate. It throws when the engine could not be asked, which is not an answer: the
 * caller says so and offers to try again, and never opens the form on it.
 */
export type Normalise = (spec: Record<string, unknown>) => Promise<unknown>;

/**
 * The builder a saved report opens in: the workspace when its spec carries a workspace state of a
 * known version and that state, normalised, produces the spec saved; the form when there is no such
 * state or it makes another spec. Rejects, as `normalise` does, when that could not be checked.
 */
export async function openSaved(
  catalogue: CustomReportCatalogue,
  spec: unknown,
  normalise: Normalise,
  clock?: (minute: number) => string,
  dates?: (span: DateSpan) => string
): Promise<Opening> {
  const member = builderOf(spec);
  if (!member) return { builder: "form", reason: "notMade" };
  const report = Report.fromState(catalogue, member.version, member.state, clock, dates);
  if (!report) return { builder: "form", reason: "notMade" };
  const produced = await normalise(report.spec());
  return produced !== null && sameJson(withoutBuilder(produced), withoutBuilder(spec))
    ? { builder: "workspace", report }
    : { builder: "form", reason: "changed" };
}

/** Asks the engine's validator for the stored form of a spec; it reads no data. */
export async function normaliseWith(
  validate: (request: { spec: Record<string, unknown> }) => Promise<{ spec?: unknown; errors?: unknown[] }>,
  spec: Record<string, unknown>
): Promise<unknown> {
  const result = await validate({ spec });
  return (result.errors ?? []).length === 0 ? (result.spec ?? null) : null;
}

/** The spec the report saves: what it produces, with its state as the `builder` member. */
export function specToSave(report: Report): Record<string, unknown> {
  return withBuilder(report.spec(), report.toState());
}

/** Whether the report's state is more than the engine keeps (`BUILDER_MAX_BYTES`), so it cannot be saved. */
export function stateTooLarge(report: Report): boolean {
  return new TextEncoder().encode(JSON.stringify(builderOf(specToSave(report)))).length > BUILDER_MAX_BYTES;
}
