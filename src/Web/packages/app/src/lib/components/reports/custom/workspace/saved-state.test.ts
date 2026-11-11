import { describe, expect, it, vi } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { Report } from "./report.svelte.js";
import { openSaved, specToSave, stateTooLarge } from "./saved-report.js";
import {
  BUILDER_KIND,
  BUILDER_VERSION,
  builderOf,
  parseSavedReport,
  sameJson,
  withBuilder,
  withoutBuilder,
  type SavedReport,
} from "./saved-state.js";
import { STARTERS, starter } from "./starters.js";

/** The state as it travels: through JSON, as the API stores and returns it. */
function travelled(report: Report): unknown {
  return JSON.parse(JSON.stringify(report.toState()));
}

/** A report with a block of every kind, one of each changed from its default. */
function everyBlock(): Report {
  const report = new Report(liveCatalogue, starter("one-food")!);
  report.title = "Every block";
  expect(report.add("figures")).toBeNull();
  expect(report.add("table")).toBeNull();
  expect(report.add("comparison")).toBeNull();
  const [chart, figures, table, comparison] = report.blocks;
  if (chart.kind !== "chart" || figures.kind !== "figures" || table.kind !== "table" || comparison.kind !== "comparison")
    throw new Error("unexpected blocks");
  chart.ws.updateMoment(0, { foods: [{ id: "f1", name: "Penne" }] });
  report.setBlockRange(figures, 168);
  report.setSource(table, "event_response");
  table.events.addMoment();
  report.setComparisonPeriods(comparison, { from: "2026-09-01", to: "2026-09-14" }, { from: "2026-10-01", to: "2026-10-14" });
  return report;
}

describe("the workspace's saved state", () => {
  it.each(STARTERS.map((s) => [s.id]))("reopens the starter %s as the same report and the same spec", (id) => {
    const report = new Report(liveCatalogue, starter(id)!);
    const state = travelled(report);

    const reopened = Report.fromState(liveCatalogue, BUILDER_VERSION, state);

    expect(reopened).not.toBeNull();
    expect(reopened!.spec()).toEqual(report.spec());
    expect(travelled(reopened!)).toEqual(state);
  });

  it("reopens a report of every kind of block, with their keys, ranges, sources and periods", () => {
    const report = everyBlock();
    const reopened = Report.fromState(liveCatalogue, BUILDER_VERSION, travelled(report))!;

    expect(reopened.spec()).toEqual(report.spec());
    expect(reopened.blocks.map((b) => [b.kind, b.key])).toEqual(report.blocks.map((b) => [b.kind, b.key]));
    expect(reopened.open).toBe(report.blocks[0].key);
  });

  it("gives a block added after reopening a key no restored block or group has", () => {
    const report = everyBlock();
    const reopened = Report.fromState(liveCatalogue, BUILDER_VERSION, travelled(report))!;
    reopened.add("chart");

    const keys = reopened.blocks.map((b) => b.key);
    expect(new Set(keys).size).toBe(keys.length);
  });

  it("keeps the saved state to ids and choices", () => {
    const state = everyBlock().toState();
    const text = JSON.stringify(state);

    expect(text).not.toContain('"panels"');
    expect(text).not.toContain('"dataset"');
    expect(text.length).toBeLessThan(8_000);
  });

  it("refuses a state of another version, not misreading it", () => {
    const state = travelled(new Report(liveCatalogue, starter("dawn-rise")!));

    expect(Report.fromState(liveCatalogue, 0, state)).toBeNull();
    expect(Report.fromState(liveCatalogue, BUILDER_VERSION + 1, state)).toBeNull();
    expect(Report.fromState(liveCatalogue, "1", state)).toBeNull();
  });

  it("refuses a state of an older shape, or one naming what the workspace does not know", () => {
    const state = travelled(new Report(liveCatalogue, starter("later-breakfast")!)) as SavedReport;
    const [chart] = state.blocks;
    if (chart.kind !== "chart") throw new Error("unexpected block");
    // The first shape kept a chart's state under `ws` and had no keys.
    const older = { ...state, blocks: [{ kind: "chart", ws: chart.chart }] };
    const extra = { ...state, open: 3 };
    const unknownSignal = { ...state, blocks: [{ ...chart, chart: { ...chart.chart, placed: [{ id: "ketones", marks: {}, relate: {} }] } }] };
    const unknownAnchor = { ...state, blocks: [{ ...chart, chart: { ...chart.chart, moments: [{ key: 1, what: "Breakfast" }] } }] };

    for (const bad of [older, extra, unknownSignal, unknownAnchor, null, "state", []])
      expect(parseSavedReport(BUILDER_VERSION, bad)).toBeNull();
  });

  it("refuses state the workspace could never have made", () => {
    const state = travelled(new Report(liveCatalogue, starter("last-24-hours")!)) as SavedReport;
    const [chart] = state.blocks;
    if (chart.kind !== "chart") throw new Error("unexpected block");
    const withChart = (patch: Record<string, unknown>) => ({ ...state, blocks: [{ ...chart, chart: { ...chart.chart, ...patch } }] });
    const hidden = {
      "a block key twice": { ...state, blocks: [chart, chart] },
      "a lane naming a signal not placed": withChart({ lanes: [...chart.chart.lanes, { id: "lane-x", family: "Body", own: false, signals: ["heartRate"] }] }),
      "a signal placed twice": withChart({ placed: [...chart.chart.placed, chart.chart.placed[0]] }),
      "a group key twice": withChart({ moments: [chart.chart.moments[0], chart.chart.moments[0]] }),
      "a profile name over 100 characters": withChart({ profileName: "p".repeat(101) }),
    };
    for (const [what, bad] of Object.entries(hidden)) expect(parseSavedReport(BUILDER_VERSION, bad), what).toBeNull();
    expect(Report.fromState(liveCatalogue, BUILDER_VERSION, { ...state, title: "t".repeat(liveCatalogue.maxTitleLength + 1) })).toBeNull();
  });
});

describe("the builder member of a spec", () => {
  it("carries the kind, version and state, and comes off for the engine", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const spec = withBuilder(report.spec(), report.toState());

    expect(builderOf(spec)).toEqual({ kind: BUILDER_KIND, version: BUILDER_VERSION, state: report.toState() });
    expect(withoutBuilder(spec)).toEqual(report.spec());
    expect(builderOf(report.spec())).toBeNull();
    expect(builderOf({ ...report.spec(), builder: { kind: "form", version: 1, state: {} } })).toBeNull();
  });

  it("compares specs whatever the order of their keys", () => {
    expect(sameJson({ a: 1, b: [{ c: null, d: "x" }] }, { b: [{ d: "x", c: null }], a: 1 })).toBe(true);
    expect(sameJson({ a: 1 }, { a: 1, b: null })).toBe(false);
    expect(sameJson([1, 2], [2, 1])).toBe(false);
  });
});

describe("opening a saved report", () => {
  /** The engine's normal form of a spec: here every panel gains a key it fills in. */
  const normalise = (spec: Record<string, unknown>) =>
    Promise.resolve({ ...spec, panels: (spec.panels as Record<string, unknown>[]).map((p) => ({ ...p, filled: null })) });

  async function savedSpec(report: Report) {
    return { ...(await normalise(report.spec())), builder: { kind: BUILDER_KIND, version: BUILDER_VERSION, state: travelled(report) } };
  }

  it("opens in the workspace when the state still makes the spec saved", async () => {
    const report = new Report(liveCatalogue, starter("meals-start-high")!);
    const opening = await openSaved(liveCatalogue, await savedSpec(report), normalise);

    expect(opening.builder).toBe("workspace");
    if (opening.builder === "workspace") expect(opening.report.spec()).toEqual(report.spec());
  });

  it("opens in the form when the spec was changed outside the workspace", async () => {
    const saved = await savedSpec(new Report(liveCatalogue, starter("meals-start-high")!));
    const changed = { ...saved, title: "Changed in the form" };

    expect(await openSaved(liveCatalogue, changed, normalise)).toEqual({ builder: "form", reason: "changed" });
  });

  it("does not answer when the check could not be made, so nothing opens in the form on a failure", async () => {
    const saved = await savedSpec(new Report(liveCatalogue, starter("meals-start-high")!));

    await expect(openSaved(liveCatalogue, saved, () => Promise.reject(new Error("offline")))).rejects.toThrow("offline");
  });

  it("opens in the form with no state, a state of another version, or a spec that does not validate", async () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    const saved = await savedSpec(report);
    const check = vi.fn(normalise);

    expect(await openSaved(liveCatalogue, withoutBuilder(saved), check)).toEqual({ builder: "form", reason: "notMade" });
    expect(await openSaved(liveCatalogue, { ...saved, builder: { ...saved.builder, version: 99 } }, check)).toEqual({ builder: "form", reason: "notMade" });
    expect(check).not.toHaveBeenCalled();
    expect((await openSaved(liveCatalogue, saved, () => Promise.resolve(null))).builder).toBe("form");
  });
});

describe("saving with the workspace's state", () => {
  it("sends the state as the spec's builder member", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);

    expect(specToSave(report)).toEqual(withBuilder(report.spec(), report.toState()));
    expect(stateTooLarge(report)).toBe(false);
  });

  it("knows a state larger than the engine keeps", () => {
    const report = new Report(liveCatalogue, starter("dawn-rise")!);
    report.title = "x".repeat(200);
    const [chart] = report.blocks;
    if (chart.kind !== "chart") throw new Error("unexpected block");
    chart.ws.state.moments = Array.from({ length: 400 }, (_, key) => ({ key, what: chart.ws.moments[0].what, foodCategory: "y".repeat(80) }));

    expect(stateTooLarge(report)).toBe(true);
  });
});
