import { afterEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import type { DashboardChartData } from "$api-clients";
import { transformChartData } from "$lib/utils/chart-data-transform";
import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
import { createStaticChartEngine, type ChartDataEngine } from "./chart-data-view.svelte";

const MINUTE = 60_000;
const FROM = Date.UTC(2026, 0, 6, 0, 0);
const TO = FROM + 6 * 60 * MINUTE;
const at = (minutes: number) => FROM + minutes * MINUTE;
const date = (minutes: number) => new Date(at(minutes));

const roots: Array<() => void> = [];

function engineFor(dto: DashboardChartData, onDataReady?: () => void): ChartDataEngine {
  let engine!: ChartDataEngine;
  roots.push(
    $effect.root(() => {
      engine = createStaticChartEngine({
        data: transformChartData(dto),
        range: { from: new Date(FROM), to: new Date(TO) },
        onDataReady,
      });
    })
  );
  flushSync();
  return engine;
}

afterEach(() => {
  while (roots.length) roots.pop()!();
});

describe("createStaticChartEngine", () => {
  it("names the window it was given and reports ready once", () => {
    const onDataReady = vi.fn();
    const engine = engineFor({ glucoseData: [{ time: at(0), sgv: 100 }] }, onDataReady);

    expect(onDataReady).toHaveBeenCalledTimes(1);
    expect(engine.lookbackHours).toBe(6);
    expect(engine.nowMinute).toBe(TO);
    expect(engine.fullXDomain.from.getTime()).toBe(FROM);
    expect(engine.displayDateRangeWithPredictions.to.getTime()).toBe(TO);
    expect(engine.predictionData).toBeNull();
    expect(engine.effectiveShowPredictions).toBe(false);
    expect(engine.predictionServiceAvailable).toBe(false);
    expect(engine.predictionError).toBeNull();
    expect(engine.chartDataError).toBeNull();
  });

  it("takes the median of an even count as the mean of the middle two", () => {
    const engine = engineFor({
      glucoseData: [80, 120, 100, 200].map((sgv, i) => ({ time: at(i * 5), sgv })),
    });
    expect(engine.medianGlucose).toBe(110);
    expect(engineFor({}).medianGlucose).toBe(100);
  });

  it("clips spans to the window and drops those outside it", () => {
    const engine = engineFor({
      profileSpans: [
        { startMills: at(-60), endMills: at(30), state: "fallback", metadata: { profileName: "Weekday" } },
        { startMills: at(30), state: "Weekend" },
        { startMills: at(-120), endMills: at(-60), state: "Gone" },
      ],
      tempBasalSpans: [
        { startMills: at(10), endMills: at(40), metadata: { absolute: 0.4, percent: 50 } },
        { startMills: at(50), endMills: at(60), metadata: { rate: "not a number" } },
      ],
    });

    const [weekday, weekend] = engine.displayProfileSpans;
    expect(engine.displayProfileSpans).toHaveLength(2);
    expect(weekday).toMatchObject({ profileName: "Weekday" });
    expect(weekday.displayStart.getTime()).toBe(FROM);
    expect(weekend).toMatchObject({ profileName: "Weekend" });
    expect(weekend.displayEnd.getTime()).toBe(TO);

    expect(engine.displayTempBasalSpans.map((s) => [s.rate, s.percent])).toEqual([
      [0.4, 50],
      [null, null],
    ]);
  });

  it("finds what is active or nearby at a time", () => {
    const engine = engineFor({
      glucoseData: [0, 5, 10].map((m) => ({ time: at(m), sgv: 100 + m })),
      bolusMarkers: [{ time: at(20), insulin: 2 }],
      carbMarkers: [{ time: at(22), carbs: 30 }],
      deviceEventMarkers: [{ time: at(24) }],
      systemEventMarkers: [{ time: at(26) }, { time: at(-30) }],
      basalSeries: [{ timestamp: at(0), rate: 0.8 }, { timestamp: at(60), rate: 1.2 }],
      pumpModeSpans: [{ startMills: at(0), endMills: at(60), state: "Manual" }],
      overrideSpans: [{ startMills: at(10), endMills: at(20), state: "Exercise" }],
      activitySpans: [
        { startMills: at(0), endMills: at(30), state: "Walk" },
        { startMills: at(15), endMills: at(45), state: "Run" },
      ],
      basalDeliverySpans: [{ startMills: at(0), endMills: at(90), rate: 0.8 }],
    });
    const { finders } = engine;

    expect(finders.findNearbyBolus(date(21))?.insulin).toBe(2);
    expect(finders.findNearbyCarbs(date(21))?.carbs).toBe(30);
    expect(finders.findNearbyDeviceEvent(date(30))).toBeUndefined();
    expect(finders.findNearbyDeviceEvent(date(25))).toBeDefined();
    expect(finders.findNearbySystemEvent(date(27))).toBeDefined();
    expect(engine.displaySystemEvents).toHaveLength(1);

    expect(finders.findActivePumpMode(date(30))?.state).toBe("Manual");
    expect(finders.findActiveOverride(date(30))).toBeUndefined();
    expect(finders.findActiveActivities(date(20)).map((s) => s.state)).toEqual(["Walk", "Run"]);
    expect(finders.findActiveProfile(date(20))).toBeUndefined();
    expect(finders.findActiveTempBasal(date(20))).toBeUndefined();
    expect(finders.findActiveBasalDelivery(date(45))?.rate).toBe(0.8);

    expect(finders.findBasalValue(engine.basalData, date(30))?.rate).toBe(0.8);
    expect(finders.findBasalValue([], date(30))).toBeUndefined();
    expect(finders.findSeriesValue(engine.glucoseData, date(4))?.sgv).toBe(105);
    expect(finders.findSeriesValue(engine.glucoseData, date(99))?.sgv).toBe(110);
    expect(finders.findPreviousGlucose(date(12))?.sgv).toBe(105);
    expect(finders.findPreviousGlucose(date(0))).toBeUndefined();
    expect(engine.scheduledBasalData.map((p) => p.rate)).toEqual([0.8, 1.2]);
  });

  it("marks basal stale from its last delivery to the end of the window", () => {
    const lastEnd = TO - STALE_THRESHOLD_MS - 10 * MINUTE;
    const engine = engineFor({
      basalDeliverySpans: [
        { startMills: at(0), endMills: at(30), rate: 1 },
        { startMills: at(30), endMills: lastEnd, rate: 1 },
      ],
    });

    expect(engine.staleBasalData?.start.getTime()).toBe(lastEnd);
    expect(engine.finders.isStaleBasalTime(new Date(TO))).toBe(true);
    expect(engine.finders.isStaleBasalTime(date(10))).toBe(false);
    expect(engineFor({}).finders.isStaleBasalTime(date(10))).toBe(false);
  });

  it("reads the pump mode of the latest span when none is active now", () => {
    const engine = engineFor({
      pumpModeSpans: [
        { startMills: at(0), endMills: at(60), state: "Manual" },
        { startMills: at(60), endMills: at(120), state: "Suspended" },
        { startMills: at(120), endMills: at(180), state: "Manual" },
      ],
    });

    expect(engine.currentPumpMode).toBe("Manual");
    expect(engine.uniquePumpModes).toEqual(["Manual", "Suspended"]);
    expect(engineFor({}).currentPumpMode).toBe("Automatic");
  });

  it("keeps tracker markers inside the window, in time order", () => {
    const engine = engineFor({
      trackerMarkers: [{ time: at(90) }, { time: at(30) }, { time: at(-30) }],
    });
    expect(engine.displayTrackerMarkers.map((m) => m.time.getTime())).toEqual([at(30), at(90)]);
  });
});
