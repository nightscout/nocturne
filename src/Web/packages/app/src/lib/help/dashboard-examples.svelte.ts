import { SvelteDate } from "svelte/reactivity";
import {
  BasalDeliveryOrigin,
  BolusKind,
  BolusType,
  BolusType2,
  ChartColor,
  ChartSpanKind,
  GlucoseTrend,
  PumpModeState,
  DashboardVisibility,
  DeviceEventType,
  TrackerCategory,
  SystemEventType,
  type DashboardChartData,
  type MultiPeriodStatistics,
} from "$lib/api";
import { RealtimeStore } from "$lib/stores/realtime-store.svelte";
import { transformChartData } from "$lib/utils/chart-data-transform";
import { resolveChartThresholds } from "$lib/constants/glucose-thresholds";
import type { PredictionData } from "$lib/api/prediction-data";

export const EXAMPLE_NOW = Date.UTC(2026, 9, 13, 12);
const hour = 60 * 60 * 1000;
const ago = (hours: number) => EXAMPLE_NOW - hours * hour;
const iso = (millis: number) => new Date(millis).toISOString();

export function exampleStatistics(automatic = true): MultiPeriodStatistics {
  const delivery = {
    totalBolus: 18,
    automaticBolus: automatic ? 4 : 0,
    totalBasal: automatic ? 24 : 20,
    tdd: automatic ? 42 : 38,
  };
  return {
    lastUpdated: iso(EXAMPLE_NOW),
    lastDay: {
      startDate: iso(ago(24)),
      endDate: iso(EXAMPLE_NOW),
      periodDays: 1,
      insulinDelivery: delivery,
      entryCount: 288,
      treatmentSummary: { totals: { food: { carbs: 160 } } },
      analytics: {
        timeInRange: {
          percentages: {
            veryLow: 1,
            low: 3,
            target: 78,
            high: 14,
            veryHigh: 4,
          },
        },
      },
    },
    last90Days: {
      startDate: iso(ago(90 * 24)),
      endDate: iso(EXAMPLE_NOW),
      periodDays: 90,
      insulinDelivery: {
        totalBolus: 1620,
        automaticBolus: automatic ? 360 : 0,
        totalBasal: automatic ? 2160 : 1800,
        tdd: automatic ? 42 : 38,
      },
      treatmentSummary: { totals: { food: { carbs: 14400 } } },
      entryCount: 25920,
      analytics: {
        timeInRange: {
          percentages: {
            veryLow: 1,
            low: 4,
            target: 74,
            high: 16,
            veryHigh: 5,
          },
        },
      },
    },
  };
}

export function exampleRealtimeStore() {
  // Constructing a store registers handlers; only initialize() opens a connection.
  const store = new RealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
  store.now = EXAMPLE_NOW;
  store.currentReadingLoaded = true;
  const {
    targetLow: _low,
    targetHigh: _high,
    ...thresholds
  } = resolveChartThresholds();
  store.chartThresholds = { ...thresholds, targetLow: 90, targetHigh: 140 };
  store.entries = Array.from({ length: 73 }, (_, i) => ({
    _id: `help-glucose-${i}`,
    type: "sgv",
    mills: ago(i / 12 + 1 / 60),
    sgv:
      i < 2
        ? 106 - i * 4
        : ([106, 120, 145, 190, 210, 150, 90, 65, 80][Math.floor(i / 9)] ??
          106),
    direction: GlucoseTrend.Flat,
    delta: 4,
    data_source: "documentation-example",
  }));
  store.carbIntakes = [
    { id: "help-meal-1", mills: ago(1), carbs: 45 },
    { id: "help-meal-2", mills: ago(4), carbs: 30 },
  ];
  store.boluses = [
    {
      id: "help-bolus",
      mills: ago(1),
      insulin: 3,
      kind: BolusKind.Manual,
      bolusType: BolusType.Normal,
    },
  ];
  store.trackerDefinitions = [
    {
      id: "help-sensor",
      name: "Sensor",
      category: TrackerCategory.Sensor,
      dashboardVisibility: DashboardVisibility.Always,
    },
    {
      id: "help-cannula",
      name: "Infusion site",
      category: TrackerCategory.Cannula,
      dashboardVisibility: DashboardVisibility.Always,
    },
  ];
  store.trackerInstances = [
    {
      id: "help-sensor-run",
      definitionId: "help-sensor",
      startedAt: iso(ago(48)),
      isActive: true,
    },
    {
      id: "help-cannula-run",
      definitionId: "help-cannula",
      startedAt: iso(ago(18)),
      isActive: true,
    },
  ];
  return store;
}

export function exampleChart(store: RealtimeStore) {
  const span = (id: string, state: string, color: ChartColor) => ({
    id,
    kind: ChartSpanKind.StateSpan,
    state,
    color,
    startMills: ago(4),
    endMills: ago(2),
  });
  const data: DashboardChartData = {
    thresholds: store.chartThresholds ?? undefined,
    glucoseData: store.entries.toReversed().map((e) => ({
      time: e.mills,
      sgv: e.sgv,
      direction: e.direction,
      dataSource: "Documentation example",
    })),
    iobSeries: [
      { timestamp: ago(6), value: 0.4 },
      { timestamp: ago(3), value: 2.5 },
      { timestamp: EXAMPLE_NOW, value: 1.2 },
    ],
    cobSeries: [
      { timestamp: ago(6), value: 0 },
      { timestamp: ago(3), value: 30 },
      { timestamp: EXAMPLE_NOW, value: 12 },
    ],
    basalSeries: [
      {
        timestamp: ago(6),
        rate: 0.8,
        scheduledRate: 0.8,
        origin: BasalDeliveryOrigin.Scheduled,
      },
      {
        timestamp: ago(3),
        rate: 1.2,
        scheduledRate: 0.8,
        origin: BasalDeliveryOrigin.Algorithm,
      },
      {
        timestamp: EXAMPLE_NOW,
        rate: 0.8,
        scheduledRate: 0.8,
        origin: BasalDeliveryOrigin.Scheduled,
      },
    ],
    bolusMarkers: [
      {
        time: ago(1),
        insulin: 3,
        bolusType: BolusType2.MealBolus,
        treatmentId: "help-bolus",
      },
      { time: ago(3), insulin: 0.2, bolusType: BolusType2.Smb },
    ],
    carbMarkers: [{ time: ago(1), carbs: 45, treatmentId: "help-meal-1" }],
    bgCheckMarkers: [{ time: ago(2), glucose: 110, glucoseType: "Finger" }],
    basalInjectionMarkers: [{ id: "help-injection", time: ago(5), units: 16 }],
    pumpModeSpans: [
      span("help-mode", PumpModeState.Automatic, ChartColor.PumpModeAutomatic),
    ],
    profileSpans: [span("help-profile", "Example profile", ChartColor.Profile)],
    overrideSpans: [
      span("help-override", "Exercise target", ChartColor.Override),
    ],
    activitySpans: [
      span("help-activity", "Exercise", ChartColor.ActivityExercise),
    ],
    systemEventMarkers: [
      {
        id: "help-alarm",
        time: ago(2),
        eventType: SystemEventType.Warning,
        description: "Example device warning",
        color: ChartColor.SystemEventWarning,
      },
    ],
    trackerMarkers: [
      {
        id: "help-tracker",
        time: ago(1.5),
        name: "Sensor check",
        category: TrackerCategory.Sensor,
        color: ChartColor.TrackerSensor,
      },
    ],
    deviceEventMarkers: [
      {
        time: ago(5),
        eventType: DeviceEventType.SiteChange,
        treatmentId: "help-site",
      },
    ],
  };
  return transformChartData(data);
}

export function examplePredictions(): PredictionData {
  const curve = (end: number) => Array.from({ length: 13 }, (_, i) => ({ timestamp: EXAMPLE_NOW + i * 5 * 60000, value: 106 + (end - 106) * i / 12 }));
  return {
    timestamp: new SvelteDate(EXAMPLE_NOW),
    currentBg: 106,
    delta: 4,
    eventualBg: 115,
    iob: 1.2,
    cob: 12,
    sensitivityRatio: 1,
    intervalMinutes: 5,
    curves: {
      main: curve(115),
      iobOnly: curve(85),
      uam: curve(140),
      cob: curve(130),
      zeroTemp: curve(100),
    },
  };
}
