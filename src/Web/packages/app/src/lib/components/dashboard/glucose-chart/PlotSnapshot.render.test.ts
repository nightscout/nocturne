import type { ComponentProps } from "svelte";
import { render } from "svelte/server";
import { describe, expect, it } from "vitest";
import PlotSnapshot from "./PlotSnapshot.svelte";
import { transformChartData } from "#lib/utils/chart-data-transform.js";

type Props = ComponentProps<typeof PlotSnapshot>;

const now = Date.UTC(2026, 9, 26, 12, 0, 0);
const readings = Array.from({ length: 12 * 12 }, (_, i) => ({
  time: now - i * 5 * 60 * 1000,
  sgv: 100 + 50 * Math.sin(i / 10),
}));

const thresholds = { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 300 };

const props = (style: Partial<Props["style"]> = {}, rest: Partial<Props> = {}): Props => ({
  chartData: transformChartData({
    glucoseData: readings,
    thresholds,
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
    areaOpacity: 0.5,
    units: "mg/dl",
    ...style,
  },
  ...rest,
});

const snapshot = (p: Props) => render(PlotSnapshot, { props: p }).body;
const circleXs = (html: string) => [...html.matchAll(/<circle cx="([-\d.]+)"/g)].map((m) => Number(m[1]));

describe("PlotSnapshot", () => {
  it("draws the glucose line at the reported size, with a point per reading", () => {
    const html = snapshot(props());

    expect(html).toMatch(/<svg width="1118" height="450"/);
    expect(html).toMatch(/<path d="M[-\d.]+,[-\d.]+C[^"]*" stroke="url\(#[^)]+\)" class="lc-path stroke-2 fill-none"/);
    expect(circleXs(html)).toHaveLength(readings.length);
  });

  it("draws the readings in the window and the two either side of it", () => {
    const twoDays = Array.from({ length: 48 * 12 }, (_, i) => ({ time: now - i * 5 * 60 * 1000, sgv: 120 }));
    const html = snapshot(
      props({}, { chartData: transformChartData({ glucoseData: twoDays, thresholds }) })
    );

    // 12 h of 5-minute readings, both ends included, and two before the window's start.
    expect(circleXs(html)).toHaveLength(12 * 12 + 1 + 2);
    expect(circleXs(html).filter((x) => x < 0)).toHaveLength(2);
  });

  it("writes only attributes onto the chart's elements", () => {
    const html = snapshot(props());

    expect(html).toContain('style="position: relative;');
    expect(html).not.toContain("[object Object]");
  });

  it("strokes a single-colour line flat and leaves points out when they are off", () => {
    const html = snapshot(props({ lineColorMode: "single", lineColor: "#123456", showPoints: false }));

    expect(html).toContain('stroke="#123456"');
    expect(html).not.toContain("<linearGradient");
    expect(html).not.toContain("<circle");
  });

  it("labels the glucose axis in the viewer's units", () => {
    const labels = [...snapshot(props({ units: "mmol" })).matchAll(/class="lc-text-tspan">([^<]*)</g)].map((m) =>
      m[1].trim()
    );
    expect(labels.slice(0, 4)).toEqual(["0", "2.8", "5.5", "8.3"]);
  });

  it("fills under the line for an area style", () => {
    expect(snapshot(props({ areaMode: "full" }))).toContain("lc-area");
  });

  it("leaves the forecast's room at the right of the window", () => {
    const latest = (html: string) => Math.max(...circleXs(html));

    expect(latest(snapshot(props({}, { forecastMinutes: 60 })))).toBeLessThan(latest(snapshot(props())));
  });
});
