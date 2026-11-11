/**
 * Opening and saving a report made in the workspace. The spec carries the workspace's state in its
 * `builder` member (see saved-state.ts); a saved report reopens in the workspace only when that
 * state still produces the spec, so a report changed anywhere else opens in the form.
 */
import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import { Report } from "./report.svelte.js";
import { BUILDER_MAX_BYTES, builderOf, sameJson, withBuilder, withoutBuilder } from "./saved-state.js";

/** Which builder a saved report opens in. */
export type Opening = { builder: "workspace"; report: Report } | { builder: "form" };

/**
 * The spec as the engine stores it (version stamped, ids assigned, defaults written), or null when it
 * does not validate. The engine keeps a report's spec in that form, so the two are compared in it.
 */
export type Normalise = (spec: Record<string, unknown>) => Promise<unknown>;

/**
 * The builder a saved report opens in: the workspace when its spec carries a workspace state of a
 * known version and that state, normalised, produces the spec saved; the form in every other case
 * (no state, a state of another version or shape, or a spec changed outside the workspace).
 */
export async function openSaved(
  catalogue: CustomReportCatalogue,
  spec: unknown,
  normalise: Normalise,
  clock?: (minute: number) => string
): Promise<Opening> {
  const member = builderOf(spec);
  if (!member) return { builder: "form" };
  const report = Report.fromState(catalogue, member.version, member.state, clock);
  if (!report) return { builder: "form" };
  const produced = await normalise(report.spec());
  return produced !== null && sameJson(withoutBuilder(produced), withoutBuilder(spec))
    ? { builder: "workspace", report }
    : { builder: "form" };
}

/** The spec the report saves: what it produces, with its state as the `builder` member. */
export function specToSave(report: Report): Record<string, unknown> {
  return withBuilder(report.spec(), report.toState());
}

/** Whether the report's state is more than the engine keeps (`BUILDER_MAX_BYTES`), so it cannot be saved. */
export function stateTooLarge(report: Report): boolean {
  return new TextEncoder().encode(JSON.stringify(builderOf(specToSave(report)))).length > BUILDER_MAX_BYTES;
}
