/**
 * Every spec the workspace produces in its tests, by name, as it sends them to the API: each starter,
 * each spec of specs.fixture.json reopened and saved, and the edits the Advanced drawers make. Written
 * to produced-specs.fixture.json, which the API's ProducedSpecRoundTripTests runs through the engine's
 * validator and normaliser; produced-specs.test.ts fails when the fixture is out of date. Rewrite it
 * with `UPDATE_FIXTURES=1 pnpm exec vitest run --config vitest.config.ts <path to produced-specs.test.ts>`.
 *
 * Block keys come from a counter (`freshKey`), so the specs are made in one fixed order in a fresh
 * module, the way a test file runs.
 */
import { liveCatalogue } from "./catalogue.fixtures.js";
import { givenSources } from "./given.svelte.js";
import { Report, type GivenBlock } from "./report.svelte.js";
import { specToSave } from "./saved-report.js";
import specs from "./specs.fixture.json";
import { STARTERS } from "./starters.js";

type Spec = Record<string, unknown>;

function fail(what: string, reason: string | null): void {
  if (reason !== null) throw new Error(`${what}: ${reason}`);
}

function given(report: Report, at = 0): GivenBlock {
  const block = report.blocks[at];
  if (block?.kind !== "given") throw new Error(`Block ${at} is not held as given.`);
  return block;
}

function layerOf(report: Report, block: GivenBlock, id: string) {
  const layer = report.givenLayers(block).find((l) => l.id === id);
  if (!layer) throw new Error(`No layer ${id}.`);
  return layer;
}

/** The edits round-trip.test.ts makes to a chart held as given, and the signal chart's "change signal by signal". */
function drawerEdits(): [string, Spec][] {
  const out: [string, Spec][] = [];

  {
    const report = Report.fromSpec(liveCatalogue, specs["template glucose"])!;
    const block = given(report);
    report.renameGivenLayer(block, report.givenLayers(block)[0], "Basal rate");
    out.push(["given glucose, a signal renamed", specToSave(report)]);
  }
  {
    const report = Report.fromSpec(liveCatalogue, specs["template glucose"])!;
    const block = given(report);
    const glucose = { dataset: { name: "glucose_series" }, field: "glucose" };
    fail("rolling average", report.addGivenCalculated(block, "rolling_mean", glucose, null, 120));
    out.push(["given glucose, a rolling average added", specToSave(report)]);
  }
  {
    const report = Report.fromSpec(liveCatalogue, specs["template treatments"])!;
    const block = given(report);
    fail("own scale", report.setGivenOwnScale(block, layerOf(report, block, "markers"), true));
    out.push(["given treatments, a signal on a scale of its own", specToSave(report)]);
  }
  {
    const report = Report.fromSpec(liveCatalogue, specs["template glucose"])!;
    const block = given(report);
    fail("remove", report.removeGivenLayer(block, layerOf(report, block, "cob")));
    out.push(["given glucose, a signal removed", specToSave(report)]);
  }
  {
    const starter = STARTERS.find((s) => s.id === "last-24-hours")!;
    const report = new Report(liveCatalogue, starter);
    const chart = report.blocks[0];
    if (chart.kind !== "chart") throw new Error("A starter opens as a chart of signals.");
    const undo = report.changeSignalBySignal(chart);
    if (typeof undo === "string") throw new Error(undo);
    out.push(["last-24-hours, changed signal by signal", specToSave(report)]);

    const block = given(report);
    const sources = givenSources(liveCatalogue, block.panel);
    const iob = sources.find((s) => s.source.field === "iob");
    if (!iob) throw new Error("The starter's chart has IOB.");
    // The chart holds the most signals a chart may, so one goes first.
    fail("remove", report.removeGivenLayer(block, report.givenLayers(block).at(-1)!));
    fail("rolling average", report.addGivenCalculated(block, "rolling_mean", iob.source, null, 60));
    out.push(["last-24-hours, signal by signal, a signal removed and a rolling average added", specToSave(report)]);
  }
  return out;
}

export function producedSpecs(): Record<string, Spec> {
  const out: [string, Spec][] = [];
  for (const starter of STARTERS) out.push([`starter ${starter.id}`, specToSave(new Report(liveCatalogue, starter))]);
  for (const [name, spec] of Object.entries(specs)) out.push([`reopened ${name}`, specToSave(Report.fromSpec(liveCatalogue, spec)!)]);
  out.push(...drawerEdits());
  // As the fixture holds them: what JSON leaves out (an undefined member) is not there.
  return Object.fromEntries(out.map(([name, spec]): [string, Spec] => [name, JSON.parse(JSON.stringify(spec))]));
}
