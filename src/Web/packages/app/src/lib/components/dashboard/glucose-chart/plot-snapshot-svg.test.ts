import { describe, expect, it } from "vitest";
import { plotSnapshotSvg, type PlotSnapshotOptions } from "./plot-snapshot-svg";
import { transformChartData } from "#lib/utils/chart-data-transform.js";

const now = Date.UTC(2026, 9, 26, 12, 0, 0);
const readings = Array.from({ length: 12 * 12 }, (_, i) => ({
  time: now - i * 5 * 60 * 1000,
  sgv: 100 + 50 * Math.sin(i / 10),
}));

const options = (style: Partial<PlotSnapshotOptions["style"]> = {}): PlotSnapshotOptions => ({
  chartData: transformChartData({
    glucoseData: readings,
    thresholds: { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300 },
  }),
  nowMs: now,
  lookbackHours: 12,
  forecastMinutes: 0,
  size: { width: 1118, height: 450 },
  compact: false,
  style: {
    lineColorMode: "continuous",
    lineColor: "#22c55e",
    pointColorMode: "continuous",
    pointColor: "#22c55e",
    showPoints: true,
    areaMode: "off",
    units: "mg/dl",
    ...style,
  },
});

describe("plotSnapshotSvg", () => {
  it("draws the glucose line the live chart draws, with a point per reading", () => {
    const svg = plotSnapshotSvg(options())!;

    expect(svg).toMatch(/^<svg viewBox="0 0 1118 450"/);
    expect(svg).toMatch(/<path d="M[-\d.]+,[-\d.]+C/);
    expect(svg).toContain('class="lc-path stroke-2 fill-none"');
    expect(svg.match(/<circle /g)).toHaveLength(readings.length);
  });

  it("strokes a single-colour line flat and leaves points out when they are off", () => {
    const svg = plotSnapshotSvg(options({ lineColorMode: "single", lineColor: "#123456", showPoints: false }))!;

    expect(svg).toContain('stroke="#123456"');
    expect(svg).not.toContain("<linearGradient");
    expect(svg).not.toContain("<circle");
  });

  it("labels the glucose axis in the viewer's units", () => {
    const labels = [...plotSnapshotSvg(options({ units: "mmol" }))!.matchAll(/dy="3.5">([^<]*)</g)].map((m) => m[1]);
    expect(labels.slice(0, 4)).toEqual(["0", "2.8", "5.5", "8.3"]);
  });

  it("draws nothing for an area style, which it does not reproduce", () => {
    expect(plotSnapshotSvg(options({ areaMode: "full" }))).toBeNull();
  });

  it("leaves the forecast's room at the right of the window", () => {
    const lastX = (svg: string) => Number(/<circle cx="([-\d.]+)"/.exec(svg)![1]);
    const without = plotSnapshotSvg(options())!;
    const withRoom = plotSnapshotSvg({ ...options(), forecastMinutes: 60 })!;

    expect(lastX(withRoom)).toBeLessThan(lastX(without));
  });

  it("draws nothing without readings", () => {
    expect(plotSnapshotSvg({ ...options(), chartData: transformChartData({ glucoseData: [] }) })).toBeNull();
  });
});
