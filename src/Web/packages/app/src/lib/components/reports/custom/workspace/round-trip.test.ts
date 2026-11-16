import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { allLayers, asObject, tracksOf } from "./given.svelte.js";
import { Report } from "./report.svelte.js";
import { specToSave } from "./saved-report.js";
import { BUILDER_VERSION, builderOf, parseSavedReport, withoutBuilder, withoutNulls } from "./saved-state.js";
import specs from "./specs.fixture.json";
import { vocabularyOf } from "./workspace.svelte.js";

/**
 * Every ready-made report and every report saved on a dev instance before the workspace was the only
 * builder, as the API returns them (normalised). Each opens in the workspace and saves the same spec.
 * What the workspace produces is compared here without the engine's normalisation, which only the API
 * has; round-trip.live.test.ts sends it through that normalisation on a running API.
 */
const SPECS: [string, Record<string, unknown>][] = Object.entries(specs);

/** A spec as the engine reads it: no builder state, no null members. */
function engineView(spec: unknown): unknown {
  return withoutNulls(withoutBuilder(spec));
}

describe("a spec opened in the workspace", () => {
  it.each(SPECS)("%s saves the spec it opened from", (_, spec) => {
    const report = Report.fromSpec(liveCatalogue, spec);
    expect(report).not.toBeNull();
    expect(engineView(specToSave(report!))).toEqual(engineView(spec));
  });

  it.each(SPECS)("%s reopens from its saved state as it was", (_, spec) => {
    const report = Report.fromSpec(liveCatalogue, spec)!;
    const saved: unknown = JSON.parse(JSON.stringify(specToSave(report)));
    const member = builderOf(saved);
    expect(member?.version).toBe(BUILDER_VERSION);
    expect(parseSavedReport(member?.version, member?.state, vocabularyOf(liveCatalogue))).not.toBeNull();
    const reopened = Report.fromState(liveCatalogue, member?.version, member?.state);
    expect(engineView(reopened?.spec())).toEqual(engineView(spec));
  });

  it.each(SPECS)("%s keeps every panel, track, layer, binding and calculated series", (_, spec) => {
    const out = asObject(engineView(Report.fromSpec(liveCatalogue, spec)!.spec())) ?? {};
    const panels = (Array.isArray(out.panels) ? out.panels : []).map((p) => asObject(p) ?? {});
    const given = (Array.isArray(spec.panels) ? spec.panels : []).map((p) => asObject(withoutNulls(p)) ?? {});
    expect(panels.map((p) => [p.id, p.kind])).toEqual(given.map((p) => [p.id, p.kind]));
    panels.forEach((panel, i) => {
      expect(tracksOf(panel).map((t) => t.id)).toEqual(tracksOf(given[i]).map((t) => t.id));
      expect(allLayers(panel)).toEqual(allLayers(given[i]));
      expect(panel.bindings).toEqual(given[i].bindings);
      expect(panel.dataset).toEqual(given[i].dataset);
    });
  });

  it("opens a panel of figures, a table or a comparison a block makes exactly as that block", () => {
    const kinds = SPECS.map(([name, spec]) => [name, Report.fromSpec(liveCatalogue, spec)!.blocks.map((b) => b.kind).join(" ")]);
    expect(Object.fromEntries(kinds)).toMatchObject({
      "template agp": "figures given given",
      "saved AGP, 90 days": "figures given given",
      "template glucose": "given",
    });
  });

  it("holds a panel whose stored form a block does not write as given", () => {
    // The stored comparison carries the range and columns the engine wrote, which a comparison block leaves to it.
    expect(Report.fromSpec(liveCatalogue, specs["template comparison"])!.blocks.map((b) => b.kind)).toEqual(["given"]);
  });

  it("holds every panel as given when asked to", () => {
    for (const [name, spec] of SPECS) {
      const report = Report.fromSpec(liveCatalogue, spec, undefined, undefined, false)!;
      expect(report.blocks.every((b) => b.kind === "given"), name).toBe(true);
      expect(engineView(report.spec()), name).toEqual(engineView(spec));
    }
  });
});

describe("a chart held as given", () => {
  const glucose = specs["template glucose"];

  function open() {
    const report = Report.fromSpec(liveCatalogue, glucose)!;
    const block = report.blocks[0];
    if (block.kind !== "given") throw new Error("The glucose template opens as a given chart.");
    return { report, block };
  }

  it("lists each signal by its name and mark", () => {
    const { report, block } = open();
    expect(report.givenLayers(block).map((l) => [l.name, l.mark])).toEqual([
      ["Basal", "area"],
      ["Target range", "band"],
      ["Glucose", "line"],
      ["Boluses and carbs", "markers"],
      ["IOB", "line"],
      ["COB", "line"],
      ["Modes and overrides", "spans"],
    ]);
  });

  it("changes how one signal is drawn and nothing else", () => {
    // The fixture leaves out each dataset's marks; these are the live catalogue's for glucose readings.
    const slots = (y: string) => [
      { key: "x", label: "", input: "field", fields: ["time"], counts: [], required: true, choices: [] },
      { key: "y", label: "", input: "fields", fields: ["glucose", "iob", "cob"], counts: [], required: true, choices: [] },
      { key: y, label: "", input: "field", fields: ["glucose"], counts: [], required: false, choices: [] },
    ];
    const marks = ["line", "area", "points"].map((name) => ({ name, defaultBindings: { x: "time", y: ["glucose"] }, bindings: slots("fit") }));
    const catalogue = {
      ...liveCatalogue,
      datasets: liveCatalogue.datasets.map((d) => (d.name === "glucose_series" ? { ...d, marks } : d)),
    };
    const report = Report.fromSpec(catalogue, glucose)!;
    const block = report.blocks[0];
    if (block.kind !== "given") throw new Error("The glucose template opens as a given chart.");
    const iob = report.givenLayers(block).find((l) => l.id === "iob")!;
    expect(iob.marks).toContain("area");
    expect(report.setGivenMark(block, iob, "area")).toBeNull();
    const before = allLayers(asObject(withoutNulls(glucose.panels[0])) ?? {}).find((l) => l.id === "iob");
    const after = report.givenLayers(block).find((l) => l.id === "iob")!;
    expect(after.mark).toBe("area");
    const layer = allLayers(block.panel).find((l) => l.id === "iob");
    expect(layer?.bindings).toEqual(before?.bindings);
  });

  it("gives a signal a scale of its own and takes it back", () => {
    const report = Report.fromSpec(liveCatalogue, specs["template treatments"])!;
    const block = report.blocks[0];
    if (block.kind !== "given") throw new Error("The treatments chart opens as a given chart.");
    const tracks = tracksOf(block.panel).length;
    const markers = report.givenLayers(block).find((l) => l.id === "markers")!;
    expect(report.setGivenOwnScale(block, markers, true)).toBeNull();
    expect(tracksOf(block.panel)).toHaveLength(tracks + 1);
    const alone = report.givenLayers(block).find((l) => l.id === "markers")!;
    expect(alone.own).toBe(true);
    expect(report.setGivenOwnScale(block, alone, false)).toBeNull();
    expect(tracksOf(block.panel)).toHaveLength(tracks);
  });

  it("refuses a fifth scale with its reason", () => {
    const { report, block } = open();
    const target = report.givenLayers(block).find((l) => l.id === "target")!;
    expect(report.setGivenOwnScale(block, target, true)).toMatch(/at most 4 scales/);
  });

  it("adds a rolling average beside its source", () => {
    const { report, block } = open();
    const glucoseSource = { dataset: { name: "glucose_series" }, field: "glucose" };
    expect(report.addGivenCalculated(block, "rolling_mean", glucoseSource, null, 120)).toBeNull();
    const added = report.givenLayers(block).find((l) => l.derived)!;
    expect(added.name).toBe("Rolling average of Glucose over 2 hours");
    expect(added.track).toBe(1);
    const layer = allLayers(block.panel).find((l) => l.id === added.id);
    expect(layer?.derived).toEqual({ op: "rolling_mean", a: glucoseSource, minutes: 120 });
  });

  it("asks for the second signal of a difference", () => {
    const { report, block } = open();
    expect(report.calculatedReason(block, "difference", { dataset: { name: "glucose_series" }, field: "iob" }, null, null)).toMatch(/second signal/);
  });

  it("keeps at least one signal", () => {
    const report = Report.fromSpec(liveCatalogue, specs["template insulin-glucose"])!;
    const block = report.blocks[0];
    if (block.kind !== "given") throw new Error("A scatter opens as a given chart.");
    expect(report.removeGivenLayer(block, report.givenLayers(block)[0])).toMatch(/at least one signal/);
  });

  it("saves a renamed signal under its new name, reopened", () => {
    const { report, block } = open();
    report.renameGivenLayer(block, report.givenLayers(block)[0], "Basal rate");
    const saved: unknown = JSON.parse(JSON.stringify(specToSave(report)));
    const member = builderOf(saved);
    const reopened = Report.fromState(liveCatalogue, member?.version, member?.state)!;
    const again = reopened.blocks[0];
    expect(again.kind === "given" && reopened.givenLayers(again)[0].name).toBe("Basal rate");
  });
});
