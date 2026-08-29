import { describe, it, expect } from "vitest";
import { mergeChartData } from "./chart-data-merge";
import type { TransformedChartData } from "./chart-data-transform";

// The dashboard loads the most recent 6 hours blocking and streams hours 6→48
// in a second payload, then merges the two. Anything the merge forgets is
// silently truncated to the 6-hour window, so these tests assert on the
// collections rather than on the merge helpers.
function chartData(overrides: Partial<TransformedChartData> = {}): TransformedChartData {
  return {
    iobSeries: [],
    cobSeries: [],
    basalSeries: [],
    glucoseData: [],
    heartRateSeries: [],
    stepSeries: [],
    bolusMarkers: [],
    carbMarkers: [],
    deviceEventMarkers: [],
    bgCheckMarkers: [],
    systemEventMarkers: [],
    trackerMarkers: [],
    basalInjectionMarkers: [],
    pumpModeSpans: [],
    profileSpans: [],
    overrideSpans: [],
    activitySpans: [],
    tempBasalSpans: [],
    basalDeliverySpans: [],
    defaultBasalRate: 1,
    thresholds: { glucoseYMax: 300 } as TransformedChartData["thresholds"],
    maxIob: 0,
    maxCob: 0,
    maxBasalRate: 0,
    ...overrides,
  } as TransformedChartData;
}

function injection(id: string, time: string, units: number) {
  return { id, time: new Date(time), units, insulinName: "Lantus" };
}

describe("mergeChartData basal injections", () => {
  it("keeps an injection that only exists in the streamed historical half", () => {
    // A once-daily long-acting dose at 00:16 falls outside the blocking
    // 6-hour window for most of the day, so it only ever arrives streamed.
    const initial = chartData({ basalInjectionMarkers: [] });
    const historical = chartData({
      basalInjectionMarkers: [injection("a", "2026-08-29T00:16:00Z", 22)],
    });

    const merged = mergeChartData(initial, historical);

    expect(merged.basalInjectionMarkers).toHaveLength(1);
    expect(merged.basalInjectionMarkers[0].id).toBe("a");
  });

  it("does not duplicate an injection present in both halves", () => {
    const shared = injection("a", "2026-08-29T00:16:00Z", 22);
    const merged = mergeChartData(
      chartData({ basalInjectionMarkers: [shared] }),
      chartData({ basalInjectionMarkers: [{ ...shared }] })
    );

    // Rendered in an {#each ... (marker.id)} block, so a duplicate id is a crash.
    expect(merged.basalInjectionMarkers).toHaveLength(1);
  });

  it("keeps injections from both halves", () => {
    const merged = mergeChartData(
      chartData({ basalInjectionMarkers: [injection("late", "2026-08-29T21:00:00Z", 4)] }),
      chartData({ basalInjectionMarkers: [injection("early", "2026-08-29T00:16:00Z", 22)] })
    );

    expect(merged.basalInjectionMarkers.map((m) => m.id)).toEqual(["early", "late"]);
  });
});

describe("mergeChartData wearable series", () => {
  it("keeps heart-rate and step samples from the historical half", () => {
    const merged = mergeChartData(
      chartData(),
      chartData({
        heartRateSeries: [{ time: new Date("2026-08-29T01:00:00Z"), bpm: 58 }],
        stepSeries: [{ time: new Date("2026-08-29T01:00:00Z"), steps: 120 }],
      })
    );

    expect(merged.heartRateSeries).toHaveLength(1);
    expect(merged.stepSeries).toHaveLength(1);
  });
});

describe("mergeChartData glucose axis", () => {
  // The server sizes glucoseYMax to the max SGV in the range it was asked for,
  // so the streamed half can need a taller axis than the blocking half. The
  // chart's yDomain clips above it, which would drop the excursion entirely.
  it("takes the taller glucose axis from either half", () => {
    const merged = mergeChartData(
      chartData({ thresholds: { glucoseYMax: 300 } as never }),
      chartData({ thresholds: { glucoseYMax: 370 } as never })
    );

    expect(merged.thresholds.glucoseYMax).toBe(370);
  });

  it("keeps the initial half's other thresholds", () => {
    const merged = mergeChartData(
      chartData({ thresholds: { glucoseYMax: 300, low: 70 } as never }),
      chartData({ thresholds: { glucoseYMax: 280, low: 80 } as never })
    );

    expect(merged.thresholds.low).toBe(70);
  });
});

describe("mergeChartData", () => {
  it("returns the initial payload untouched when nothing streamed", () => {
    const initial = chartData({
      basalInjectionMarkers: [injection("a", "2026-08-29T21:00:00Z", 4)],
    });

    expect(mergeChartData(initial, null)).toBe(initial);
  });
});
