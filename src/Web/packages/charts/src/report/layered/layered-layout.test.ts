import { describe, expect, it } from "vitest";
import type { ReportLayer, ReportPanel, ReportTrack } from "../types.js";
import {
  AXIS_CLEARANCE,
  bandSets,
  barRects,
  legend,
  MINUTE_ORIGIN,
  placeGlyphs,
  rowMatch,
  rowsAt,
  sharedValueExtents,
  sideScales,
  slots,
  spanRects,
  toDomain,
  trackBands,
  xExtent,
} from "./layered-layout.js";
import { COHORT_HUES, cohortBands } from "../percentile-bands.js";

const glucose = { name: "glucose", label: "Glucose", kind: "number" as const, unit: "mg/dL", color: "glucose-in-range" as const, glucoseScale: true };
const iob = { name: "iob", label: "IOB", kind: "number" as const, unit: "U", color: "insulin-bolus" as const };
const time = { name: "time", label: "Time", kind: "time" as const };

function layer(over: Partial<ReportLayer>): ReportLayer {
  return {
    id: "l",
    title: "Layer",
    mark: "line",
    bindings: { x: "time", y: ["glucose"] },
    xKind: "time",
    unit: "mg/dL",
    side: "left",
    status: "ok",
    window: { from: 0, to: 1 },
    fields: [time, glucose],
    rows: [],
    ...over,
  };
}

const track = (layers: ReportLayer[], height = 1): ReportTrack => ({ id: "t", height, layers });

describe("a layered chart's layout", () => {
  it("shares the plot's height among tracks by their weights, with a gap between them", () => {
    const bands = trackBands([{ height: 2 }, { height: 1 }, { height: 0.5 }], 378, 10);

    expect(bands.map((b) => Math.round(b.height))).toEqual([200, 100, 50]);
    expect(bands[1].top).toBeCloseTo(210);
    expect(bands[2].bottom).toBeCloseTo(370);
  });

  it("keeps the lowest track clear of the time axis, so its zero does not sit on the axis labels", () => {
    const [only] = trackBands([{ height: 1 }], 120);

    expect(only.bottom).toBe(120 - AXIS_CLEARANCE);
    expect(AXIS_CLEARANCE).toBeGreaterThanOrEqual(6);
  });

  it("gives a track a left and a right scale, each over its own layers, the glucose side reaching the high limit", () => {
    const band = { top: 0, bottom: 100, height: 100 };
    const t = track([
      layer({ id: "g", rows: [{ time: 0, glucose: 120 }] }),
      layer({ id: "i", side: "right", unit: "U", bindings: { x: "time", y: ["iob"] }, fields: [time, iob], rows: [{ time: 0, iob: 2 }] }),
    ]);

    const { left, right } = sideScales(t, band, 180);

    expect(left?.unit).toBe("mg/dL");
    expect(left?.domain[1]).toBeGreaterThanOrEqual(180);
    expect(right?.unit).toBe("U");
    expect(right?.domain[1]).toBeLessThan(3);
    expect(left?.toPixel(0)).toBe(100);
    expect(right?.toPixel(0)).toBe(100);
  });

  it("keeps a side for a layer that was not read, with no values on it", () => {
    const t = track([layer({ id: "g", status: "not_permitted", rows: [] })]);

    expect(sideScales(t, { top: 0, bottom: 50, height: 50 }, null).left?.domain[0]).toBe(0);
  });

  it("lays minutes of the day and from an event on a time axis from one origin", () => {
    expect(toDomain("time_of_day", 90)).toEqual(new Date(MINUTE_ORIGIN + 90 * 60_000));
    expect(toDomain("offset", -30)).toEqual(new Date(MINUTE_ORIGIN - 30 * 60_000));
    expect(toDomain("value", 4.2)).toBe(4.2);
    expect(toDomain("time", 1000)).toEqual(new Date(1000));
  });

  it("spans the window over time, the whole day over a time of day, and the rows elsewhere", () => {
    const rows = (xs: number[]) => xs.map((x) => ({ x, value: 1 }));
    const panel = (xKind: ReportPanel["xKind"], xs: number[]): Pick<ReportPanel, "tracks" | "window" | "xKind"> => ({
      xKind,
      window: { from: 10, to: 20 },
      tracks: [track([layer({ bindings: { x: "x", y: ["value"] }, rows: rows(xs) })])],
    });

    expect(xExtent(panel("time", [1, 2]))).toEqual([10, 20]);
    expect(xExtent(panel("time_of_day", [1, 2]))).toEqual([0, 1440]);
    expect(xExtent(panel("lag", [-180, 0, 180]))).toEqual([-180, 180]);
    expect(xExtent(panel("day", [0, 86_400_000]))).toEqual([0, 2 * 86_400_000]);
  });

  it("finds every series' row at the slot nearest the pointer, and none past the tolerance", () => {
    const bands = layer({
      mark: "band",
      bindings: { x: "minute", y: ["p50"], edges: ["p25", "p75"], series: "series" },
      rows: [
        { minute: 0, series: "A", p50: 1 },
        { minute: 0, series: "B", p50: 2 },
        { minute: 5, series: "A", p50: 3 },
      ],
    });

    const match = { tolerance: 5, markerTolerance: 1, gridded: false };
    expect(rowsAt(bands, 1, match).map((r) => r.series)).toEqual(["A", "B"]);
    expect(rowsAt(bands, 4, match).map((r) => r.p50)).toEqual([3]);
    expect(rowsAt(bands, 40, match)).toEqual([]);
  });

  it("gives a step line's tooltip the last row at or before the pointer, the value the step draws there", () => {
    const rate = { name: "rate", label: "Basal rate", kind: "number" as const, unit: "U/h", step: true };
    const basal = layer({ bindings: { x: "time", y: ["rate"] }, fields: [time, rate], rows: [{ time: 0, rate: 0.8 }, { time: 100, rate: 1.2 }] });
    const match = { tolerance: 10, markerTolerance: 1, gridded: false };

    expect(rowsAt(basal, 90, match).map((r) => r.rate)).toEqual([0.8]);
    expect(rowsAt(basal, 400, match).map((r) => r.rate)).toEqual([1.2]);
    expect(rowsAt(basal, -1, match)).toEqual([]);
  });

  it("matches a gridded panel's slot exactly, never a neighbouring slot", () => {
    const folded = layer({ rows: [{ time: 0, glucose: 100 }, { time: 900_000, glucose: 120 }] });
    const match = { tolerance: 1_800_000, markerTolerance: 1, gridded: true };

    expect(rowsAt(folded, 900_000, match).map((r) => r.glucose)).toEqual([120]);
    expect(rowsAt(folded, 1_800_000, match)).toEqual([]);
  });

  it("gives a treatment marker its own narrow tolerance, so a marker far from the pointer is not listed", () => {
    const markers = layer({ mark: "markers", bindings: { x: "time", y: ["iob"] }, fields: [time, iob], rows: [{ time: 3_600_000, iob: 2 }] });
    const panel = { tracks: [track([markers])], window: { from: 0, to: 86_400_000 }, xKind: "time" as const, grid: null };
    const match = rowMatch(panel, 7_200_000);

    expect(match.markerTolerance).toBe(864_000);
    expect(rowsAt(markers, 3_600_000 + 600_000, match)).toHaveLength(1);
    expect(rowsAt(markers, 3_600_000 + 3_000_000, match)).toEqual([]);
    expect(rowMatch({ ...panel, grid: 15 }, 1).gridded).toBe(true);
    expect(rowMatch({ ...panel, xKind: "day", grid: 15 }, 1).gridded).toBe(false);
  });

  it("lists the slots every drawn layer has a row at, once each, in order", () => {
    const panel = {
      tracks: [
        track([layer({ rows: [{ time: 5 }, { time: 1 }] }), layer({ id: "x", status: "failed", rows: [{ time: 99 }] })]),
        track([layer({ id: "b", rows: [{ time: 1 }, { time: 3 }] })]),
      ],
    };

    expect(slots(panel)).toEqual([1, 3, 5]);
  });

  it("colours each cohort of a band in its cohort's hue and draws a comparison range apart", () => {
    const sets = bandSets(layer({
      mark: "band",
      bindings: { x: "minute", y: ["p50"], edges: ["p25", "p75"], series: "series" },
      rows: [
        { minute: 0, series: "Lunch", cohort: 2, p50: 1 },
        { minute: 0, series: "Lunch", cohort: 2, range: "comparison", p50: 1 },
        { minute: 0, series: "Dinner", cohort: 1, p50: 1 },
      ],
    }));

    expect(sets.map((s) => [s.label, s.comparison, s.line])).toEqual([
      ["Lunch", false, "var(--chart-3)"],
      ["Lunch", true, "var(--chart-3)"],
      ["Dinner", false, "var(--chart-1)"],
    ]);
  });

  it("keeps the field's colour for a band of one group and no comparison range", () => {
    const one = bandSets(layer({
      mark: "band",
      bindings: { x: "minute", y: ["glucose"], edges: ["glucose", "glucose"], series: "series" },
      rows: [
        { minute: 0, series: "Lows", cohort: 1, glucose: 1 },
        { minute: 5, series: "Lows", cohort: 1, glucose: 2 },
      ],
    }));
    expect(one.map((s) => [s.label, s.line])).toEqual([["Lows", "var(--glucose-in-range)"]]);

    const withRange = bandSets(layer({
      mark: "band",
      bindings: { x: "minute", y: ["p50"], edges: ["p25", "p75"], series: "series" },
      rows: [
        { minute: 0, series: "Lows", cohort: 1, p50: 1 },
        { minute: 0, series: "Lows", cohort: 1, range: "comparison", p50: 1 },
      ],
    }));
    expect(withRange.map((s) => s.line)).toEqual(["var(--chart-1)", "var(--chart-1)"]);
  });

  it("gives a band with no series the layer's colour, or the hue the API gave it", () => {
    expect(bandSets(layer({ mark: "band" }))[0].line).toBe("var(--glucose-in-range)");
    expect(bandSets(layer({ mark: "band", color: "chart-4" }))[0].line).toBe("var(--chart-4)");
  });

  it("stacks bars on a continuous axis from the last part's top, each in its field's colour", () => {
    const bars = layer({
      mark: "bars",
      bindings: { x: "time", y: ["glucose", "iob"], stacked: true },
      fields: [time, glucose, iob],
      rows: [{ time: 0, glucose: 2, iob: 3 }, { time: 10, glucose: 1, iob: 1 }],
    });
    const rects = barRects(bars, (x) => x * 10, (v) => 100 - v * 10, 200);

    expect(rects.map((r) => [r.y, r.height, r.color])).toEqual([
      [80, 20, "var(--glucose-in-range)"],
      [50, 30, "var(--insulin-bolus)"],
      [90, 10, "var(--glucose-in-range)"],
      [80, 10, "var(--insulin-bolus)"],
    ]);
  });

  it("centres a day's bar on the middle of the day, inside the axis that runs to the end of the last day", () => {
    const day = { name: "day", label: "Day", kind: "day" as const };
    const bars = layer({
      mark: "bars",
      xKind: "day",
      bindings: { x: "day", y: ["iob"] },
      fields: [day, iob],
      rows: [{ day: 0, iob: 1 }, { day: 86_400_000, iob: 2 }],
    });
    const rects = barRects(bars, (x) => x / 864_000, (v) => 100 - v, 200);

    expect(rects.map((r) => r.x + r.width / 2)).toEqual([50, 150]);
  });

  it("puts each span in its category's lane, in the row's own colour", () => {
    const spans = layer({
      mark: "spans",
      bindings: { start: "start", end: "end", category: "kind" },
      fields: [
        { name: "kind", label: "Kind", kind: "category" },
        { name: "label", label: "Name", kind: "category", colorField: "color" },
        { name: "start", label: "Start", kind: "time" },
        { name: "end", label: "End", kind: "time" },
      ],
      rows: [
        { kind: "Pump mode", label: "Automatic", start: 0, end: 10, color: "pump-mode-automatic" },
        { kind: "Override", label: "Exercise", start: 2, end: 4, color: "not-a-colour" },
      ],
    });

    const { lanes, spans: rects } = spanRects(spans, { top: 0, bottom: 40, height: 40 }, (x) => x * 10);

    expect(lanes.map((l) => [l.name, l.y])).toEqual([["Pump mode", 0], ["Override", 20]]);
    expect(rects.map((r) => [r.x, r.width, r.label, r.color])).toEqual([
      [0, 100, "Automatic", "var(--pump-mode-automatic)"],
      [20, 20, "Exercise", "var(--muted-foreground)"],
    ]);
  });

  it("sets a marker on the track's line at the nearest reading, a carb above a bolus", () => {
    const line = layer({ rows: [{ time: 0, glucose: 100 }, { time: 10, glucose: 200 }] });
    const markers = layer({
      id: "m",
      mark: "markers",
      side: "none",
      markers: [{ time: 9, kind: "bolus", amount: 2 }, { time: 9, kind: "carbs", amount: 30 }],
    });
    const t = track([line, markers]);

    const glyphs = placeGlyphs(markers, t, { top: 0, bottom: 300, height: 300 }, (x) => x, () => (v) => 300 - v);

    expect(glyphs.map((g) => [g.kind, g.y])).toEqual([["bolus", 100], ["carbs", 66]]);
  });

  it("names each drawn layer in the legend, with no swatch for a glucose line or a lane", () => {
    const entries = legend({
      tracks: [track([
        layer({ id: "g", title: "Glucose" }),
        layer({ id: "h", title: "Glucose again", color: "chart-1", context: "Glucose, mean per 15 minutes" }),
        layer({ id: "s", title: "Modes", mark: "spans", side: "none" }),
        layer({ id: "x", title: "Hidden", status: "not_permitted" }),
      ])],
    });

    expect(entries.map((e) => [e.title, e.color])).toEqual([
      ["Glucose", null],
      ["Glucose again", "var(--chart-1)"],
      ["Modes", null],
    ]);
    expect(entries.map((e) => e.context)).toEqual([null, "Glucose, mean per 15 minutes", null]);
  });
});

describe("small multiples", () => {
  const band = { top: 0, bottom: 100, height: 100 };
  const panel = (glucoseValues: number[], iobUnit = "U") => ({
    tracks: [
      {
        id: "lane-glucose",
        height: 2,
        layers: [
          layer({ id: "g", rows: glucoseValues.map((glucose, i) => ({ time: i, glucose })) }),
          layer({ id: "i", side: "right", unit: iobUnit, bindings: { x: "time", y: ["iob"] }, fields: [time, { ...iob, unit: iobUnit }], rows: [{ time: 0, iob: 2 }] }),
        ],
      },
    ],
    thresholds: null,
  });

  it("gives every panel's side one scale across the panels when the unit is the same", () => {
    const shared = sharedValueExtents([panel([100, 140]), panel([90, 300])]);

    expect(shared["lane-glucose"].left).toEqual([0, 300]);
    const quiet = sideScales(panel([100, 140]).tracks[0], band, null, shared["lane-glucose"]);
    const loud = sideScales(panel([90, 300]).tracks[0], band, null, shared["lane-glucose"]);
    expect(quiet.left?.domain).toEqual(loud.left?.domain);
    expect(quiet.left?.domain[1]).toBeGreaterThanOrEqual(300);
  });

  it("keeps a side's own scale where the panels draw it in different units", () => {
    const shared = sharedValueExtents([panel([100], "U"), panel([100], "g")]);

    expect(shared["lane-glucose"].right).toBeUndefined();
    expect(shared["lane-glucose"].left).toEqual([0, 100]);
  });

  it("draws a scale from its own values when nothing is shared", () => {
    expect(sideScales(panel([100, 140]).tracks[0], band, null).left?.domain[1]).toBeLessThan(300);
  });
});

describe("event group hues", () => {
  it("are the engine's five group tokens in order, the sixth group again as the first", () => {
    expect(COHORT_HUES).toEqual(["var(--chart-1)", "var(--chart-3)", "var(--chart-2)", "var(--chart-4)", "var(--chart-5)"]);
    expect(cohortBands(5).median).toBe(cohortBands(0).median);
    expect(new Set([0, 1, 2, 3, 4].map((i) => cohortBands(i).median)).size).toBe(5);
  });
});
