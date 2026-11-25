import { describe, it, expect } from "vitest";
import { transformChartData } from "./transform";
import { BasalDeliveryOrigin, DeviceEventType } from "./enums";

describe("transformChartData", () => {
  it("turns mills into dates and keeps glucose in mg/dL", () => {
    const data = transformChartData({
      glucoseData: [{ time: 1_700_000_000_000, sgv: 120, direction: "Flat", dataSource: "dexcom" }],
      iobSeries: [{ timestamp: 1_700_000_300_000, value: 1.5 }],
    });

    expect(data.glucoseData).toEqual([
      {
        time: new Date(1_700_000_000_000),
        sgv: 120,
        direction: "Flat",
        dataSource: "dexcom",
        color: "var(--glucose-in-range)",
      },
    ]);
    expect(data.iobSeries).toEqual([{ time: new Date(1_700_000_300_000), value: 1.5 }]);
  });

  it("colours each reading by the payload's own thresholds", () => {
    const data = transformChartData({
      thresholds: { veryLow: 54, low: 80, high: 160, veryHigh: 250 },
      glucoseData: [{ time: 0, sgv: 75 }, { time: 1, sgv: 170 }],
    });

    expect(data.glucoseData.map((p) => p.color)).toEqual([
      "var(--glucose-low)",
      "var(--glucose-high)",
    ]);
  });

  it("resolves the API's colour names to custom properties, with defaults", () => {
    const data = transformChartData({
      basalSeries: [{ timestamp: 0, rate: 0.8, origin: BasalDeliveryOrigin.Algorithm, fillColor: "insulin-temp-basal" }],
      deviceEventMarkers: [{ time: 0, eventType: DeviceEventType.SiteChange }],
      pumpModeSpans: [{ startMills: 0, state: "Automatic", color: "pump-mode-automatic" }],
    });

    expect(data.basalSeries[0]).toMatchObject({
      fillColor: "var(--insulin-temp-basal)",
      strokeColor: "var(--insulin-basal)",
      origin: "Algorithm",
    });
    expect(data.deviceEventMarkers[0].color).toBe("var(--muted-foreground)");
    expect(data.pumpModeSpans[0]).toMatchObject({
      startTime: new Date(0),
      endTime: null,
      color: "var(--pump-mode-automatic)",
    });
  });

  it("fills absent series and scales with the defaults the chart draws with", () => {
    const data = transformChartData({});

    expect(data.glucoseData).toEqual([]);
    expect(data.bolusMarkers).toEqual([]);
    expect(data.basalDeliverySpans).toEqual([]);
    expect(data.lastPumpSyncMills).toBeNull();
    expect({
      defaultBasalRate: data.defaultBasalRate,
      maxBasalRate: data.maxBasalRate,
      maxIob: data.maxIob,
      minIob: data.minIob,
      maxCob: data.maxCob,
    }).toEqual({ defaultBasalRate: 1, maxBasalRate: 3, maxIob: 5, minIob: 0, maxCob: 100 });
  });
});
