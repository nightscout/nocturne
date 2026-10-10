import { describe, expect, it } from "vitest";
import { transformChartData } from "$lib/utils/chart-data-transform";
import type { Entry } from "$lib/websocket/types";
import { mergeRealtimeGlucose } from "./merge-glucose";

const thresholds = { veryLow: 54, low: 70, high: 180, veryHigh: 250 };

const entries: Entry[] = [
  { _id: "a", type: "sgv", mills: 1_000, sgv: 60 },
  { _id: "b", type: "sgv", mills: 2_000, sgv: 120 },
  { _id: "c", type: "sgv", mills: 3_000, sgv: 300 },
  { _id: "d", type: "sgv", mills: 9_000, sgv: 100 },
];

describe("mergeRealtimeGlucose", () => {
  it("draws nothing before either the server series or thresholds are known", () => {
    expect(mergeRealtimeGlucose(null, entries, 0, 10_000)).toEqual([]);
  });

  it("draws the readings in the window alone, coloured by the given thresholds, when there is no server series", () => {
    const points = mergeRealtimeGlucose(null, entries, 1_000, 3_000, thresholds);

    expect(points.map((p) => [p.time.getTime(), p.sgv, p.color])).toEqual([
      [1_000, 60, "var(--glucose-low)"],
      [2_000, 120, "var(--glucose-in-range)"],
      [3_000, 300, "var(--glucose-very-high)"],
    ]);
  });

  it("colours the readings it adds by the server's thresholds when none are given", () => {
    const chartData = transformChartData({ thresholds: { ...thresholds, high: 100 } });

    const points = mergeRealtimeGlucose(chartData, entries, 2_000, 2_000);

    expect(points.map((p) => p.color)).toEqual(["var(--glucose-high)"]);
  });
});
