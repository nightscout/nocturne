/**
 * The round trip through the engine's own normalisation, which round-trip.test.ts cannot make: the
 * normaliser is the API's (ReportSpecValidator, behind POST /api/v4/reports/custom/validate), so a node
 * test has no copy of it to run. This test sends each spec the workspace produces to a running API and
 * compares what comes back. It is skipped unless NOCTURNE_REPORTS_API_URL (the API's base URL),
 * NOCTURNE_REPORTS_API_HOST (the tenant host) and NOCTURNE_REPORTS_API_TOKEN (a member's bearer token)
 * are set; against `aspire start`, take the token from the dev-only login (see the dev-tenant-api skill).
 * In CI the API's ProducedSpecRoundTripTests runs the same specs (produced-specs.fixture.json) through the
 * validator itself.
 */
import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import { specToSave } from "./saved-report.js";
import { builderOf, withoutBuilder, withoutNulls } from "./saved-state.js";
import specs from "./specs.fixture.json";
import { STARTERS } from "./starters.js";

const url = process.env.NOCTURNE_REPORTS_API_URL;
const host = process.env.NOCTURNE_REPORTS_API_HOST;
const token = process.env.NOCTURNE_REPORTS_API_TOKEN;

/** The spec as the engine normalises it, or the errors it gave. */
async function normalise(spec: Record<string, unknown>): Promise<{ spec: unknown; errors: unknown[] }> {
  const response = await fetch(`${url}/api/v4/reports/custom/validate`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`, "X-Forwarded-Host": host ?? "" },
    body: JSON.stringify({ spec }),
  });
  expect(response.status).toBe(200);
  const body = (await response.json()) as { spec?: unknown; errors?: unknown[] };
  return { spec: body.spec ?? null, errors: body.errors ?? [] };
}

function engineView(spec: unknown): unknown {
  return withoutNulls(withoutBuilder(spec));
}

describe.skipIf(!url || !host || !token)("a produced spec through the engine's normalisation", () => {
  it.each(Object.entries(specs))("%s normalises to the spec it opened from", async (_, spec) => {
    const produced = specToSave(Report.fromSpec(liveCatalogue, spec)!);
    const normalised = await normalise(produced);
    expect(normalised.errors).toEqual([]);
    expect(engineView(normalised.spec)).toEqual(engineView(spec));
  });

  it.each(STARTERS.map((s) => [s.id, s] as const))("starter %s normalises, reopens and produces the same spec", async (_, s) => {
    const produced = specToSave(new Report(liveCatalogue, s));
    const normalised = await normalise(produced);
    expect(normalised.errors).toEqual([]);
    const member = builderOf(normalised.spec);
    const reopened = Report.fromState(liveCatalogue, member?.version, member?.state);
    expect(reopened).not.toBeNull();
    const again = await normalise(specToSave(reopened!));
    expect(engineView(again.spec)).toEqual(engineView(normalised.spec));
  });
});
