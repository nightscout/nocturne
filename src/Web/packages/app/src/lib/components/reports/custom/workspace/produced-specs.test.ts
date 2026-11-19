import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { producedSpecs } from "./produced-specs.js";
import { Report } from "./report.svelte.js";
import { specToSave } from "./saved-report.js";
import { builderOf, withoutBuilder, withoutNulls } from "./saved-state.js";

const FIXTURE = fileURLToPath(new URL("./produced-specs.fixture.json", import.meta.url));

const produced = producedSpecs();

describe("the produced-specs fixture the API's round-trip test reads", () => {
  it("holds every spec the workspace produces now", () => {
    if (process.env.UPDATE_FIXTURES === "1") writeFileSync(FIXTURE, `${JSON.stringify(produced, null, 2)}\n`);
    const fixture: unknown = existsSync(FIXTURE) ? JSON.parse(readFileSync(FIXTURE, "utf8")) : null;
    expect(fixture, "Out of date: rewrite it with UPDATE_FIXTURES=1 (see produced-specs.ts)").toEqual(produced);
  });

  // The API keeps the builder's state as sent (ProducedSpecRoundTripTests), so a saved report reopens from this state.
  it.each(Object.entries(produced))("%s reopens from its builder state and saves the same spec", (_, spec) => {
    const member = builderOf(spec);
    const reopened = Report.fromState(liveCatalogue, member?.version, member?.state);
    expect(reopened).not.toBeNull();
    expect(withoutNulls(withoutBuilder(specToSave(reopened!)))).toEqual(withoutNulls(withoutBuilder(spec)));
  });
});
