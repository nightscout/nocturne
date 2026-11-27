import { describe, expect, it } from "vitest";
import type { ReportLayer, ReportPanel, ReportTrack } from "../types.js";
import {
  AXIS_CLEARANCE,
  AXIS_ROOM,
  axisGroups,
  bandParts,
  bandSets,
  barRects,
  groupAt,
  groupSpan,
  hasValues,
  labelledGlyphs,
  laneNames,
  legend,
  legendDescription,
  nearestSlot,
  MINUTE_ORIGIN,
  paintOrder,
  placeGlyphs,
  rawAcross,
  rowMatch,
  rowsAt,
  sharedValueExtents,
  sideScales,
  slots,
  spanRects,
  tipEntries,
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

  it("draws a span of one instant as a tick, and finds it within a marker's tolerance", () => {
    const events = layer({
      mark: "spans",
      bindings: { start: "start", end: "end", category: "kind" },
      rows: [
        { kind: "Site", start: 100, end: 100 },
        { kind: "Mode", start: 0, end: 400 },
      ],
    });
    const match = { tolerance: 5, markerTolerance: 10, gridded: false };

    expect(spanRects(events, { top: 0, bottom: 20, height: 20 }, (x) => x).spans[0].width).toBe(2);
    expect(rowsAt(events, 104, match).map((r) => r.kind)).toEqual(["Site", "Mode"]);
    expect(rowsAt(events, 106, match).map((r) => r.kind)).toEqual(["Mode"]);
    expect(rowsAt(events, 401, match)).toEqual([]);
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

describe("tracks along different axes", () => {
  const DAY = 86_400_000;
  const dayLayer = (rows: { day: number; hours: number }[]) =>
    layer({
      id: "sleep",
      mark: "bars",
      xKind: "day",
      bindings: { x: "day", y: ["hours"] },
      fields: [{ name: "day", label: "Night", kind: "day" }, { name: "hours", label: "Sleep", kind: "number", unit: "h" }],
      rows,
    });
  const panelOf = (tracks: ReportTrack[], xKind: ReportPanel["xKind"] = "time"): ReportPanel => ({
    id: "p",
    kind: "chart",
    status: "ok",
    window: { from: 0, to: 2 * DAY },
    fields: [],
    rows: [],
    summary: "",
    xKind,
    tracks,
  });

  it("puts consecutive tracks along one axis in one group, and starts a group where the axis changes", () => {
    const panel = panelOf([
      { ...track([layer({})]), id: "a", xKind: "time" },
      { ...track([dayLayer([])]), id: "b", xKind: "time" },
      { ...track([layer({ xKind: "time_of_day" })]), id: "c", xKind: "time_of_day" },
    ]);

    const groups = axisGroups(panel);

    expect(groups.map((g) => [g.kind, g.first, g.panel.tracks.map((t) => t.id)])).toEqual([
      ["time", 0, ["a", "b"]],
      ["time_of_day", 2, ["c"]],
    ]);
    expect(groups[1].panel.xKind).toBe("time_of_day");
  });

  it("draws a track along days beside tracks along time on their time axis, as one group", () => {
    const panel = panelOf([
      { ...track([layer({})]), id: "a", xKind: "time" },
      { ...track([dayLayer([])]), id: "b", xKind: "day" },
    ]);

    expect(axisGroups(panel).map((g) => [g.kind, g.panel.xKind, g.panel.tracks.length])).toEqual([["time", "time", 2]]);
  });

  it("takes the panel's axis for a track that carries none, so an older result is one group", () => {
    expect(axisGroups(panelOf([track([layer({})]), track([layer({})])])).length).toBe(1);
  });

  it("reads each group at the same horizontal position: the same fraction of its own extent", () => {
    const panel = panelOf([
      { ...track([layer({})]), id: "a", xKind: "time" },
      { ...track([layer({ xKind: "time_of_day" })]), id: "b", xKind: "time_of_day" },
    ]);

    expect(rawAcross(axisGroups(panel), DAY)).toEqual([DAY, 720]);
  });

  it("finds a day's row anywhere in the day on a time track, and not past its end", () => {
    const sleep = dayLayer([{ day: 0, hours: 7 }, { day: DAY, hours: 6 }]);
    const match = rowMatch(panelOf([track([sleep])]), 5 * 60_000);

    expect(rowsAt(sleep, 15 * 3_600_000, match)).toEqual([{ day: 0, hours: 7 }]);
    expect(rowsAt(sleep, DAY + 1, match)).toEqual([{ day: DAY, hours: 6 }]);
    expect(rowsAt(sleep, 3 * DAY, match)).toEqual([]);
  });

  it("cuts a day's bar to the plot where the window starts or ends inside the day", () => {
    const sleep = dayLayer([{ day: 0, hours: 7 }]);
    const x = (raw: number) => ((raw - DAY / 2) / DAY) * 100;

    const [bar] = barRects(sleep, x, (v) => 100 - v, 100);

    expect(bar.x).toBe(0);
    expect(bar.width).toBeCloseTo(40);
  });

  it("leaves room for a bottom axis above each track that starts a new axis group", () => {
    const plain = trackBands([{ height: 1 }, { height: 1 }], 300, 10);
    const split = trackBands([{ height: 1 }, { height: 1 }], 300, 10, [true, true]);

    expect(split[1].top - split[0].bottom).toBe(10 + AXIS_ROOM);
    expect(split[1].bottom).toBeCloseTo(plain[1].bottom);
  });
});

describe("a chart's description", () => {
  const entries = [
    { title: "Low", unit: "mg/dL", group: null },
    { title: "High", unit: "mg/dL", group: "Breakfast" },
    { title: "Mean", unit: null, group: null },
  ];

  it("lists the layers as one image, in the interface language", () => {
    expect(legendDescription(entries, { locale: "en", language: "en", hour12: false })).toBe(
      "Low (mg/dL), High (mg/dL, Breakfast), and Mean"
    );
    expect(legendDescription(entries, { locale: "en", language: "de", hour12: false })).toBe(
      "Low (mg/dL), High (mg/dL, Breakfast) und Mean"
    );
  });

  it("uses the regional format's words when no language is given", () => {
    expect(legendDescription(entries, { locale: "de-DE", hour12: false })).toBe("Low (mg/dL), High (mg/dL, Breakfast) und Mean");
  });

  it("is empty for a chart with no layers", () => {
    expect(legendDescription([], { locale: "en", hour12: false })).toBe("");
  });
});

describe("a band's legend", () => {
  const p25 = { name: "p25", label: "Glucose P25", kind: "number" as const, unit: "mg/dL" };
  const p75 = { name: "p75", label: "Glucose P75", kind: "number" as const, unit: "mg/dL" };
  const median = { name: "p50", label: "Glucose median", kind: "number" as const, unit: "mg/dL" };
  const band = (over: Partial<ReportLayer>) =>
    layer({
      mark: "band",
      color: "cat-1" as ReportLayer["color"],
      bindings: { x: "time", y: ["p50"], edges: ["p25", "p75"] },
      fields: [time, median, p25, p75],
      spans: [{ label: "Glucose P25–P75", low: "p25", high: "p75" }],
      ...over,
    });

  it("names the middle line and the shaded span, each in the colour it is drawn in", () => {
    const [set] = bandSets(band({}));
    const parts = bandParts(band({}));

    expect(parts.map((p) => [p.label, p.shape, p.color])).toEqual([
      ["Glucose median", "line", set.line],
      ["Glucose P25–P75", "fill", set.inner],
    ]);
    expect(legend({ tracks: [track([band({})])] })[0].parts).toEqual(parts);
  });

  it("gives a middle line coloured by glucose no one colour, so its swatch shows the glucose colours", () => {
    const coloured = band({ color: null, fields: [time, { ...median, glucoseScale: true }, p25, p75] });

    expect(bandParts(coloured)[0]).toMatchObject({ shape: "line", color: null });
  });

  it("names nothing more for a mark that is not a band", () => {
    expect(bandParts(layer({}))).toEqual([]);
  });
});

describe("the tooltip's entries", () => {
  const p25 = { name: "p25", label: "Glucose P25, days with exercise", kind: "number" as const, unit: "mg/dL" };
  const p75 = { name: "p75", label: "Glucose P75, days with exercise", kind: "number" as const, unit: "mg/dL" };
  const median = { name: "p50", label: "Glucose median, days with exercise", kind: "number" as const, unit: "mg/dL" };
  const text = (value: unknown, field: { unit?: string | null }, unit: boolean) => `${value}${unit && field.unit ? ` ${field.unit}` : ""}`;
  const band = layer({
    id: "exercise",
    title: "Days with exercise",
    mark: "band",
    color: "cat-1" as ReportLayer["color"],
    bindings: { x: "time", y: ["p50"], edges: ["p25", "p75"] },
    fields: [time, median, p25, p75],
  });

  it("gives a band one entry: its name, its middle value and its shaded span's ends, with its sentence under it", () => {
    const entries = tipEntries(band, [{ time: 0, p50: 152, p25: 118, p75: 185, sentence: "At 07:05: 4 readings." }], text);

    expect(entries).toEqual([
      expect.objectContaining({ label: "Days with exercise", value: "152 mg/dL", range: "118–185", notes: ["At 07:05: 4 readings."] }),
    ]);
  });

  it("gives a band of edges only one entry: its name and its two values, not each edge's field name", () => {
    const low = { name: "targetLow", label: "Target range low", kind: "number" as const, unit: "mg/dL" };
    const high = { name: "targetHigh", label: "Target range high", kind: "number" as const, unit: "mg/dL" };
    const target = layer({ id: "target", title: "Target range", mark: "band", bindings: { x: "time", edges: ["targetLow", "targetHigh"] }, fields: [time, low, high] });

    const entries = tipEntries(target, [{ time: 0, targetLow: 70, targetHigh: 180 }], text);

    expect(entries.map((e) => [e.label, e.value, e.range])).toEqual([["Target range", "70–180 mg/dL", null]]);
  });

  it("lists each field of a line, and puts the sentences under the layer's last entry only", () => {
    const line = layer({ bindings: { x: "time", y: ["glucose", "iob"] }, fields: [time, glucose, iob], title: "Glucose" });

    const entries = tipEntries(line, [{ time: 0, glucose: 120, iob: 1.5, sentence: "One." }], text);

    expect(entries.map((e) => [e.label, e.value, e.range, e.notes])).toEqual([
      ["Glucose · Glucose", "120 mg/dL", null, []],
      ["Glucose · IOB", "1.5 U", null, ["One."]],
    ]);
  });
});

describe("what the chart names", () => {
  const text = (value: unknown, field: { unit?: string | null }, unit: boolean) => `${value}${unit && field.unit ? ` ${field.unit}` : ""}`;

  it("names each span in the tooltip by its lane, not by the layer", () => {
    const spans = layer({
      id: "modes",
      title: "Modes and overrides",
      mark: "spans",
      bindings: { start: "start", end: "end", category: "kind" },
      fields: [
        { name: "kind", label: "Kind", kind: "category" },
        { name: "label", label: "Name", kind: "category", colorField: "color" },
      ],
    });

    const entries = tipEntries(spans, [{ kind: "Pump mode", label: "Automatic" }, { kind: "Profile", label: "Default" }], text);

    expect(entries.map((e) => [e.label, e.value])).toEqual([
      ["Pump mode", "Automatic"],
      ["Profile", "Default"],
    ]);
  });

  it("adds a series' name to a tooltip entry only where the layer draws more than one", () => {
    const one = layer({ title: "Insulin on board", bindings: { x: "time", y: ["iob"], series: "series" }, fields: [time, iob], rows: [{ time: 0, iob: 1, series: "Lows" }] });
    const two = { ...one, rows: [...one.rows, { time: 0, iob: 2, series: "Highs" }] };

    expect(tipEntries(one, one.rows, text).map((e) => e.label)).toEqual(["Insulin on board"]);
    expect(tipEntries(two, two.rows, text).map((e) => e.label)).toEqual(["Insulin on board · Lows", "Insulin on board · Highs"]);
  });

  it("gives a layer's group in the legend and the tooltip where the API names one", () => {
    const grouped = layer({ title: "Glucose", group: "Days with a low", rows: [{ time: 0, glucose: 100 }] });
    const panel = { tracks: [track([grouped])] };

    expect(legend(panel)[0]).toEqual(expect.objectContaining({ title: "Glucose", unit: "mg/dL", group: "Days with a low" }));
    expect(tipEntries(grouped, grouped.rows, text)[0].label).toBe("Glucose · Days with a low");
  });

  it("runs minutes from an event to the end of the last slot, so the window's last tick is drawn", () => {
    const offset = layer({ xKind: "offset", bindings: { x: "minute", y: ["glucose"] }, rows: [-240, -235, 110, 115].map((minute) => ({ minute, glucose: 100 })) });

    expect(xExtent({ tracks: [track([offset])], window: { from: 0, to: 1 }, xKind: "offset" })).toEqual([-240, 120]);
  });

  it("knows a chart whose rows hold no values, so it draws no empty axes", () => {
    expect(hasValues({ tracks: [track([layer({ rows: [{ time: 0, glucose: null }] })])] })).toBe(false);
    expect(hasValues({ tracks: [track([layer({ rows: [{ time: 0, glucose: 90 }] })])] })).toBe(true);
  });

  it("lists every spans lane's name, so the plot leaves room for the longest", () => {
    const spans = layer({ mark: "spans", bindings: { start: "s", end: "e", category: "kind" }, rows: [{ kind: "Pump mode" }, { kind: "Override" }, { kind: "Pump mode" }] });

    expect(laneNames({ tracks: [track([spans])] })).toEqual(["Pump mode", "Override"]);
  });
});

describe("the pointer over tracks along different axes", () => {
  const groups = [{ kind: "time" as const, first: 0, panel: {} as ReportPanel }, { kind: "value" as const, first: 2, panel: {} as ReportPanel }];
  const bands = [
    { top: 0, bottom: 100, height: 100 },
    { top: 118, bottom: 150, height: 32 },
    { top: 192, bottom: 300, height: 108 },
  ];

  it("spans a group's tracks and no other's", () => {
    expect(groupSpan(groups, bands, 0)).toEqual({ top: 0, bottom: 150 });
    expect(groupSpan(groups, bands, 1)).toEqual({ top: 192, bottom: 300 });
  });

  it("is over the group whose tracks or bottom axis it reaches", () => {
    expect(groupAt(groups, bands, 60)).toBe(0);
    expect(groupAt(groups, bands, 150 + AXIS_ROOM)).toBe(0);
    expect(groupAt(groups, bands, 200)).toBe(1);
  });

  it("snaps to the nearest slot of a group", () => {
    expect(nearestSlot([0, 60, 120], 70)).toBe(60);
    expect(nearestSlot([], 70)).toBeNull();
  });
});

describe("treatment amounts", () => {
  it("writes an amount only where it is clear of the last one written of its kind", () => {
    const glyph = (key: string, x: number, kind: "bolus" | "carbs" = "bolus") => ({ key, x, y: 0, kind, amount: 1, automatic: false });

    const written = labelledGlyphs([glyph("a", 0), glyph("b", 10), glyph("c", 31), glyph("d", 12, "carbs")], 30);

    expect([...written].sort()).toEqual(["a", "c", "d"]);
  });
});

describe("paint order", () => {
  it("paints a track's fills under its lines and points, keeping the spec's order within each", () => {
    const t = track([
      layer({ id: "glucose", mark: "line" }),
      layer({ id: "tir", mark: "bars" }),
      layer({ id: "dots", mark: "points" }),
      layer({ id: "band", mark: "band" }),
      layer({ id: "iob", mark: "line" }),
    ]);

    expect(paintOrder(t).map((l) => l.id)).toEqual(["band", "tir", "glucose", "iob", "dots"]);
    expect(legend({ tracks: [t] }).map((e) => e.id)).toEqual(["glucose", "tir", "dots", "band", "iob"]);
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
