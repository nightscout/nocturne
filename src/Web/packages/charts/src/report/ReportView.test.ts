import { createRawSnippet } from "svelte";
import { render } from "svelte/server";
import { describe, expect, it } from "vitest";
import type { ChartLabels } from "../labels.js";
import type { ReportViewLabels } from "./report-view-labels.js";
import { inkPattern } from "../print/patterns.js";
import { bandSets, barRects, legend as legendOf, panelTextures } from "./layered/layered-layout.js";
import ReportView from "./ReportView.svelte";
import type { ReportLayer, ReportPanel, ReportResult } from "./types.js";

const offset = { name: "offset", label: "Minutes from the event", kind: "offset" as const };
const glucose = { name: "glucose", label: "Glucose", kind: "number" as const, unit: "mg/dL", color: "glucose-in-range" as const };

function layer(id: string, values: number[]): ReportLayer {
  return {
    id,
    title: id,
    mark: "line",
    bindings: { x: "offset", y: ["glucose"] },
    xKind: "offset",
    unit: "mg/dL",
    side: "left",
    status: "ok",
    window: { from: 0, to: 1 },
    fields: [offset, glucose],
    rows: values.map((v, i) => ({ offset: i * 5, glucose: v })),
  };
}

function panel(id: string, layers: ReportLayer[]): ReportPanel {
  return {
    id,
    title: id,
    kind: "chart",
    status: "ok",
    window: { from: 0, to: 1 },
    fields: [],
    rows: [],
    xKind: "offset",
    tracks: [{ id: "lane-glucose", height: 2, layers }],
    summary: "",
    facts: [{ label: "Events", value: "12" }],
    sentence: "Each event from 60 minutes before to 300 minutes after it.",
  };
}

function report(panels: ReportPanel[]): ReportResult {
  return { spec: { version: 1 }, units: "mg/dl", panels, summary: "" };
}

const labels: ReportViewLabels = {
  // The chart's words are read only by treatment markers, which these panels do not have.
  chart: {} as ChartLabels,
  noData: "No data",
  fullSummary: "Full summary",
};

const format = { locale: "en", hour12: false };

function html(props: Partial<Parameters<typeof ReportView>[1]> & { report: ReportResult }): string {
  return render(ReportView, { props: { format, labels, ...props } }).body;
}

const two = report([panel("a", [layer("low", [100, 110]), layer("high", [120, 130])])]);

describe("a report view", () => {
  it("draws each chart's legend and each panel's facts by default", () => {
    const body = html({ report: two });

    expect(body).toContain('data-testid="report-legend"');
    expect(body).toContain('data-testid="report-facts"');
  });

  it("leaves out the legend and the facts when the host shows them itself", () => {
    const body = html({ report: two, legend: false, facts: false });

    expect(body).not.toContain('data-testid="report-legend"');
    expect(body).not.toContain('data-testid="report-facts"');
    expect(body).toContain('data-testid="report-layered-chart"');
  });

  const grouped = (p: ReportPanel, group: string): ReportPanel => ({ ...p, group });

  it("stacks panels without a group, and lays out a run of one group as a grid of small charts", () => {
    const a = panel("a", [layer("g", [100, 140])]);
    const b = panel("b", [layer("g", [90, 300])]);
    const stack = html({ report: report([a, b]) });
    const grid = html({ report: report([grouped(a, "events"), grouped(b, "events")]) });

    expect(stack).not.toContain('data-layout="multiples"');
    expect(grid).toContain('data-layout="multiples"');
    expect(grid).toContain('data-group="events"');
    expect(grid).toMatch(/class="grid[^"]*@2xl:grid-cols-2/);
    const heights = (body: string) => [...body.matchAll(/--plot-height: (\d+)px/g)].map((m) => Number(m[1]));
    expect(Math.max(...heights(grid))).toBeLessThan(Math.min(...heights(stack)) / 2);
  });

  it("lays out only consecutive panels of one group together, each run with its own scale", () => {
    const body = html({
      report: report([
        grouped(panel("a1", [layer("g", [100])]), "a"),
        grouped(panel("a2", [layer("g", [400])]), "a"),
        panel("lone", [layer("g", [100])]),
        grouped(panel("b1", [layer("g", [100])]), "b"),
        grouped(panel("b2", [layer("g", [120])]), "b"),
      ]),
    });

    expect(body.match(/data-testid="report-group"/g)).toHaveLength(2);
    expect(body.match(/data-layout="multiples"/g)).toHaveLength(2);
    // The lone panel is full size, between the two grids.
    expect(body.indexOf('id="panel-lone"')).toBeGreaterThan(body.indexOf('data-group="a"'));
    expect(body.indexOf('id="panel-lone"')).toBeLessThan(body.indexOf('data-group="b"'));
  });

  it("does not break a small chart across printed pages", () => {
    const body = html({ report: report([grouped(panel("a", [layer("g", [100])]), "g"), grouped(panel("b", [layer("g", [120])]), "g")]) });

    expect(body.match(/class="[^"]*break-inside-avoid[^"]*" data-testid="report-panel"/g)).toHaveLength(2);
  });

  it("says a sentence every small multiple shares once, under the grid", () => {
    const a = panel("a", [layer("g", [100])]);
    const b = panel("b", [layer("g", [120])]);

    expect(html({ report: report([a, b]) }).match(/data-testid="report-sentence"/g)).toHaveLength(2);
    expect(html({ report: report([grouped(a, "g"), grouped(b, "g")]) }).match(/data-testid="report-sentence"/g)).toHaveLength(1);
  });

  it("heads each panel with the host's words in place of the engine's title", () => {
    const panels = report([panel("engine name", [layer("g", [100])])]);
    const heading = createRawSnippet((p: () => ReportPanel) => ({ render: () => `<span>Group A: ${p().id.length} letters</span>` }));

    const body = html({ report: panels, heading });

    expect(body).toContain("Group A: 11 letters");
    expect(body).not.toMatch(/>engine name</);
  });
});

describe("a series the result textures", () => {
  const day = { name: "day", label: "Day", kind: "day" as const };
  const total = { name: "total", label: "Total insulin", kind: "number" as const, unit: "U", color: "insulin-bolus" as const };
  const bolus = { name: "bolus", label: "Total bolus", kind: "number" as const, unit: "U", color: "insulin-bolus" as const };

  function bars(id: string, field: typeof total, texture?: string): ReportLayer {
    return {
      id,
      title: field.label,
      mark: "bars",
      bindings: { x: "day", y: [field.name] },
      xKind: "day",
      unit: "U",
      side: "left",
      status: "ok",
      window: { from: 0, to: 1 },
      fields: [day, texture ? { ...field, texture } : field],
      rows: [0, 1, 2].map((i) => ({ day: Date.UTC(2026, 0, 1 + i), [field.name]: 10 + i })),
    };
  }

  const insulin = report([
    { ...panel("daily", [bars("total", total), bars("bolus", bolus, "cat-2")]), xKind: "day", window: { from: Date.UTC(2026, 0, 1), to: Date.UTC(2026, 0, 4) } },
  ]);

  it("hatches the later of two bars of one colour, and only that one", () => {
    const [plain, hatched] = insulin.panels[0].tracks[0].layers;
    const rects = (l: ReportLayer) => barRects(l, (v) => v / 1e6, (v) => 100 - v, 300);

    expect(rects(plain).map((r) => r.texture)).toEqual([null, null, null]);
    expect(rects(hatched).map((r) => r.texture)).toEqual(["cat-2", "cat-2", "cat-2"]);
    expect(rects(hatched).map((r) => r.color)).toEqual(rects(plain).map((r) => r.color));
    expect(panelTextures(insulin.panels[0])).toEqual(["cat-2"]);
    // cat-2 is a back slash at a gap of 6; layerchart draws a positive turn as one.
    expect(inkPattern("cat-2")).toEqual(expect.objectContaining({ id: "chart-ink-cat-2", size: 6, lines: [expect.objectContaining({ rotate: 45 })] }));
    expect(inkPattern("in-range")).toBeNull();
  });

  it("hatches a band by its layer's or its fields' texture, and the legend says so", () => {
    const band: ReportLayer = { ...layer("wide", [100]), mark: "band", bindings: { x: "offset", y: ["glucose"], edges: ["glucose", "glucose"] }, texture: "cat-3" };

    expect(bandSets(band).map((s) => s.texture)).toEqual(["cat-3"]);
    expect(legendOf({ tracks: [{ id: "t", height: 1, layers: [band] }] })[0].texture).toBe("cat-3");
    expect(bandSets(layer("plain", [100]))[0].texture).toBeNull();
  });

  it("shows the same hatch in the legend's swatch", () => {
    const body = html({ report: insulin });
    const legend = body.slice(body.indexOf('data-testid="report-legend"'));

    expect(legend).toMatch(/data-texture="cat-2"/);
    expect(legend).toContain("repeating-linear-gradient");
  });

  it("ignores a texture name this build does not know", () => {
    const body = html({ report: report([{ ...panel("daily", [bars("bolus", bolus, "no-such-texture")]), xKind: "day" }]) });

    expect(body).not.toContain("data-texture=");
    expect(body).not.toContain("<pattern");
  });
});
