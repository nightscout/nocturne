import { render } from "svelte/server";
import { describe, expect, it } from "vitest";
import type { ChartLabels } from "../labels.js";
import type { ReportViewLabels } from "./report-view-labels.js";
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

  it("stacks the panels by default, and lays out small multiples as a grid of small charts", () => {
    const panels = report([panel("a", [layer("g", [100, 140])]), panel("b", [layer("g", [90, 300])])]);
    const stack = html({ report: panels });
    const grid = html({ report: panels, layout: "multiples" });

    expect(stack).toContain('data-layout="stack"');
    expect(grid).toContain('data-layout="multiples"');
    expect(grid).toMatch(/class="grid[^"]*@2xl:grid-cols-2/);
    const heights = (body: string) => [...body.matchAll(/--plot-height: (\d+)px/g)].map((m) => Number(m[1]));
    expect(Math.max(...heights(grid))).toBeLessThan(Math.min(...heights(stack)) / 2);
  });

  it("says a sentence every small multiple shares once, under the grid", () => {
    const panels = report([panel("a", [layer("g", [100])]), panel("b", [layer("g", [120])])]);

    expect(html({ report: panels }).match(/data-testid="report-sentence"/g)).toHaveLength(2);
    expect(html({ report: panels, layout: "multiples" }).match(/data-testid="report-sentence"/g)).toHaveLength(1);
  });
});
