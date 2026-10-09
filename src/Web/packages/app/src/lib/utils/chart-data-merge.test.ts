import { describe, it, expect } from "vitest";
import { mergeChartData, replaceWindow } from "./chart-data-merge";
import type { TransformedChartData } from "./chart-data-transform";
import { resolveChartThresholds } from "$lib/constants/glucose-thresholds";

function thresholds(
  overrides: Partial<TransformedChartData["thresholds"]> = {}
): TransformedChartData["thresholds"] {
  return { ...resolveChartThresholds(), ...overrides };
}

// The dashboard loads the most recent 6 hours blocking and streams hours 6→48
// in a second payload, then merges the two. Anything the merge forgets is
// silently truncated to the 6-hour window, so these tests assert on the
// collections rather than on the merge helpers.
function chartData(
  overrides: Partial<TransformedChartData> = {}
): TransformedChartData {
  return {
    iobSeries: [],
    cobSeries: [],
    basalSeries: [],
    glucoseData: [],
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
    thresholds: thresholds(),
    maxIob: 0,
    maxCob: 0,
    maxBasalRate: 0,
    ...overrides,
  };
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
      chartData({
        basalInjectionMarkers: [injection("late", "2026-08-29T21:00:00Z", 4)],
      }),
      chartData({
        basalInjectionMarkers: [injection("early", "2026-08-29T00:16:00Z", 22)],
      })
    );

    expect(merged.basalInjectionMarkers.map((m) => m.id)).toEqual([
      "early",
      "late",
    ]);
  });
});

describe("mergeChartData glucose axis", () => {
  // The server sizes glucoseYMax to the max SGV in the range it was asked for,
  // so the streamed half can need a taller axis than the blocking half. The
  // chart's yDomain clips above it, which would drop the excursion entirely.
  it("takes the taller glucose axis from either half", () => {
    const merged = mergeChartData(
      chartData({ thresholds: thresholds({ glucoseYMax: 300 }) }),
      chartData({ thresholds: thresholds({ glucoseYMax: 370 }) })
    );

    expect(merged.thresholds.glucoseYMax).toBe(370);
  });

  it("keeps the initial half's other thresholds", () => {
    const merged = mergeChartData(
      chartData({ thresholds: thresholds({ glucoseYMax: 300, low: 70 }) }),
      chartData({ thresholds: thresholds({ glucoseYMax: 280, low: 80 }) })
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

describe("replaceWindow", () => {
  const T = (iso: string) => new Date(iso);
  const start = T("2026-08-29T10:00:00Z").getTime();
  const bolus = (iso: string, insulin: number) => ({ time: T(iso), insulin }) as never;

  it("shows a bolus moved within the window once", () => {
    const current = chartData({ bolusMarkers: [bolus("2026-08-29T10:00:00Z", 2)] });
    const recent = chartData({ bolusMarkers: [bolus("2026-08-29T10:05:00Z", 2)] });

    const result = replaceWindow(current, recent, start);

    expect(result.bolusMarkers.map((m) => m.time.toISOString())).toEqual([
      "2026-08-29T10:05:00.000Z",
    ]);
  });

  it("drops a bolus the server no longer returns and keeps older rows", () => {
    const current = chartData({
      bolusMarkers: [bolus("2026-08-29T08:00:00Z", 1), bolus("2026-08-29T11:00:00Z", 3)],
    });

    const result = replaceWindow(current, chartData(), start);

    expect(result.bolusMarkers.map((m) => m.insulin)).toEqual([1]);
  });

  it("does not duplicate a span that began before the window and is still running", () => {
    const span = (startIso: string) => ({
      id: "p1",
      startTime: T(startIso),
      endTime: null,
    }) as never;
    const result = replaceWindow(
      chartData({ pumpModeSpans: [span("2026-08-29T08:00:00Z")] }),
      chartData({ pumpModeSpans: [span("2026-08-29T10:00:00Z")] }),
      start
    );

    expect(result.pumpModeSpans).toHaveLength(1);
  });

  it("lets the scale maxima fall back to the data that remains", () => {
    const point = (iso: string, value: number) => ({ time: T(iso), value });
    const result = replaceWindow(
      chartData({
        iobSeries: [point("2026-08-29T08:00:00Z", 2), point("2026-08-29T11:00:00Z", 9)],
        maxIob: 9,
      }),
      chartData({ iobSeries: [point("2026-08-29T11:00:00Z", 1)], maxIob: 1 }),
      start
    );

    expect(result.maxIob).toBe(2);
  });

  it("sizes the glucose axis from the fresh response and the readings that remain", () => {
    const reading = (iso: string, sgv: number) =>
      ({ time: T(iso), sgv, color: "" }) as never;
    const result = replaceWindow(
      chartData({
        glucoseData: [reading("2026-08-29T08:00:00Z", 330), reading("2026-08-29T11:00:00Z", 400)],
        thresholds: thresholds({ glucoseYMax: 400 }),
      }),
      chartData({ thresholds: thresholds({ glucoseYMax: 300 }) }),
      start
    );

    // The 400 reading sat in the replaced window; 330 plus the server's 20 of headroom remains.
    expect(result.thresholds.glucoseYMax).toBe(350);
  });

  it("ignores fresh rows before the window so a start mismatch cannot duplicate them", () => {
    const early = bolus("2026-08-29T09:58:00Z", 2);
    const result = replaceWindow(
      chartData({ bolusMarkers: [early] }),
      chartData({ bolusMarkers: [early, bolus("2026-08-29T10:01:00Z", 3)] }),
      start
    );

    expect(result.bolusMarkers.map((m) => m.insulin)).toEqual([2, 3]);
  });

  it("draws a treatment moved from before the window into it once", () => {
    const marker = (iso: string, treatmentId: string) =>
      ({ time: T(iso), insulin: 2, treatmentId }) as never;
    const result = replaceWindow(
      chartData({ bolusMarkers: [marker("2026-08-29T03:00:00Z", "t1")] }),
      chartData({ bolusMarkers: [marker("2026-08-29T11:00:00Z", "t1")] }),
      start
    );

    expect(result.bolusMarkers.map((m) => m.treatmentId)).toEqual(["t1"]);
    expect(result.bolusMarkers[0].time.toISOString()).toBe("2026-08-29T11:00:00.000Z");
  });

  it("does the same for markers keyed by id", () => {
    const tracker = (iso: string, id: string) => ({ id, time: T(iso) }) as never;
    const result = replaceWindow(
      chartData({ trackerMarkers: [tracker("2026-08-29T03:00:00Z", "k1")] }),
      chartData({ trackerMarkers: [tracker("2026-08-29T11:00:00Z", "k1")] }),
      start
    );

    expect(result.trackerMarkers).toHaveLength(1);
  });

  it("keeps older markers whose ids are empty", () => {
    const marker = (iso: string) => ({ time: T(iso), insulin: 1, treatmentId: "" }) as never;
    const result = replaceWindow(
      chartData({ bolusMarkers: [marker("2026-08-29T03:00:00Z")] }),
      chartData({ bolusMarkers: [marker("2026-08-29T11:00:00Z")] }),
      start
    );

    expect(result.bolusMarkers).toHaveLength(2);
  });
});

describe("mergeChartData treatments across the boundary", () => {
  const marker = (iso: string, treatmentId: string) =>
    ({ time: new Date(iso), insulin: 2, treatmentId }) as never;

  it("draws a treatment that moved across the boundary between the two fetches once", () => {
    const merged = mergeChartData(
      chartData({ bolusMarkers: [marker("2026-08-29T10:00:00Z", "t1")] }),
      chartData({ bolusMarkers: [marker("2026-08-29T04:00:00Z", "t1")] })
    );

    expect(merged.bolusMarkers).toHaveLength(1);
  });

  it("keeps historical markers that have no id", () => {
    const merged = mergeChartData(
      chartData({ bolusMarkers: [marker("2026-08-29T10:00:00Z", "")] }),
      chartData({ bolusMarkers: [marker("2026-08-29T04:00:00Z", "")] })
    );

    expect(merged.bolusMarkers).toHaveLength(2);
  });
});

describe("one copy of an id on each side of the boundary", () => {
  type Lists =
    | "bolusMarkers"
    | "carbMarkers"
    | "bgCheckMarkers"
    | "deviceEventMarkers"
    | "systemEventMarkers"
    | "trackerMarkers"
    | "basalInjectionMarkers";
  const cases: { list: Lists; key: "treatmentId" | "id" }[] = [
    { list: "bolusMarkers", key: "treatmentId" },
    { list: "carbMarkers", key: "treatmentId" },
    { list: "bgCheckMarkers", key: "treatmentId" },
    { list: "deviceEventMarkers", key: "treatmentId" },
    { list: "systemEventMarkers", key: "id" },
    { list: "trackerMarkers", key: "id" },
    { list: "basalInjectionMarkers", key: "id" },
  ];
  const row = (key: string, iso: string) => ({ [key]: "x1", time: new Date(iso) }) as never;
  const boundary = new Date("2026-08-29T10:00:00Z").getTime();

  it.each(cases)("replaceWindow keeps one $list row", ({ list, key }) => {
    const result = replaceWindow(
      chartData({ [list]: [row(key, "2026-08-29T03:00:00Z")] }),
      chartData({ [list]: [row(key, "2026-08-29T11:00:00Z")] }),
      boundary
    );

    expect(result[list]).toHaveLength(1);
  });

  it.each(cases)("mergeChartData keeps one $list row", ({ list, key }) => {
    const merged = mergeChartData(
      chartData({ [list]: [row(key, "2026-08-29T11:00:00Z")] }),
      chartData({ [list]: [row(key, "2026-08-29T03:00:00Z")] })
    );

    expect(merged[list]).toHaveLength(1);
  });
});
