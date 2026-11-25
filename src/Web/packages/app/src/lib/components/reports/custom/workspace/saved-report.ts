/**
 * Opening and saving a report in the workspace. The spec carries the workspace's state in its
 * `builder` member (see saved-state.ts); a saved report reopens from that state when the state still
 * produces the spec, and from the spec alone (`Report.fromSpec`) when it carries none or the spec was
 * changed elsewhere since.
 */
import type { CustomReportCatalogue } from "@nocturne/api-client";
import { Report } from "./report.svelte.js";
import type { DateSpan } from "./workspace.svelte.js";
import { BUILDER_MAX_BYTES, builderOf, sameJson, withBuilder, withoutBuilder, withoutNulls } from "./saved-state.js";

/**
 * The spec as the engine stores it (version stamped, ids assigned, defaults written), or null when it
 * does not validate. It throws when the engine could not be asked, which is not an answer: the
 * caller says so and offers to try again.
 */
export type Normalise = (spec: Record<string, unknown>) => Promise<unknown>;

/** Whether two specs are the same as the engine reads them: the builder member and null members aside. */
export function sameSpec(a: unknown, b: unknown): boolean {
  return sameJson(withoutNulls(withoutBuilder(a)), withoutNulls(withoutBuilder(b)));
}

/**
 * The report a saved spec opens as. From its workspace state when that state, normalised, produces
 * the spec saved; else from the spec, with each panel a block of figures, a table or a comparison makes
 * exactly opened as one, and when that does not reproduce the spec, every panel held as given. Null
 * when the spec has no panels to open. Rejects, as `normalise` does, when that could not be checked.
 */
export async function openSaved(
  catalogue: CustomReportCatalogue,
  spec: unknown,
  normalise: Normalise,
  clock?: (minute: number) => string,
  dates?: (span: DateSpan) => string
): Promise<Report | null> {
  const member = builderOf(spec);
  const restored = member ? Report.fromState(catalogue, member.version, member.state, clock, dates) : null;
  if (restored) {
    const produced = await normalise(restored.spec());
    if (produced !== null && sameSpec(produced, spec)) return restored;
  }
  const bare = withoutBuilder(spec);
  const report = Report.fromSpec(catalogue, bare, clock, dates);
  if (!report) return null;
  const produced = await normalise(report.spec());
  return produced !== null && sameSpec(produced, spec) ? report : Report.fromSpec(catalogue, bare, clock, dates, false);
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
