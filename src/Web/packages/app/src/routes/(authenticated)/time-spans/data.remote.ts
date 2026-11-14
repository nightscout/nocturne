/**
 * Reads for the therapy timeline. Each category has its own read scope, so a
 * read the caller is refused comes back null and the page leaves that part out.
 */
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import {
  DeviceEventType,
  StateSpanCategory,
  type DeviceEvent,
  type SleepSession,
  type StateSpan,
} from "#lib/api/index.js";
import { readable } from "#lib/server/patient-timezone.js";

const windowSchema = z.object({
  from: z.number(),
  to: z.number(),
});

/** A stretch of time drawn in a context lane. */
export interface LaneSpan {
  id: string;
  label: string;
  startMills: number;
  endMills: number;
  color: string;
}

/** A device change drawn as a point in the device lane. */
export interface LaneMarker {
  id: string;
  mills: number;
  eventType: DeviceEventType;
}

const PUMP_MODE_COLORS: Record<string, string> = {
  Automatic: "var(--pump-mode-automatic)",
  Limited: "var(--pump-mode-limited)",
  Manual: "var(--pump-mode-manual)",
  Boost: "var(--pump-mode-boost)",
  EaseOff: "var(--pump-mode-ease-off)",
  Sleep: "var(--pump-mode-sleep)",
  Exercise: "var(--pump-mode-exercise)",
  Suspended: "var(--pump-mode-suspended)",
  Off: "var(--pump-mode-off)",
  Liberty: "var(--pump-mode-liberty)",
};

const ACTIVITY_COLORS: Partial<Record<StateSpanCategory, string>> = {
  [StateSpanCategory.Exercise]: "var(--pump-mode-exercise)",
  [StateSpanCategory.Illness]: "var(--system-event-warning)",
  [StateSpanCategory.Travel]: "var(--chart-3)",
};

/** The device changes that start a new sensor, site, reservoir, pod or battery. */
const LIFECYCLE_EVENTS: ReadonlySet<DeviceEventType> = new Set([
  DeviceEventType.SensorStart,
  DeviceEventType.SensorChange,
  DeviceEventType.SiteChange,
  DeviceEventType.CannulaChange,
  DeviceEventType.InsulinChange,
  DeviceEventType.ReservoirChange,
  DeviceEventType.PodChange,
  DeviceEventType.PumpBatteryChange,
  DeviceEventType.TransmitterSensorInsert,
]);

function clip(
  spans: StateSpan[] | null | undefined,
  from: number,
  to: number,
  label: (span: StateSpan) => string,
  color: (span: StateSpan) => string
): LaneSpan[] {
  return (spans ?? [])
    .filter((s) => (s.endMills ?? to) > from && (s.startMills ?? 0) < to)
    .map((s, i) => ({
      id: s.id ?? `${s.category}-${s.startMills}-${i}`,
      label: label(s),
      startMills: Math.max(s.startMills ?? 0, from),
      endMills: Math.min(s.endMills ?? to, to),
      color: color(s),
    }));
}

/** The profile versions and changes of the range, and its glucose in 480 buckets. */
export const getTimelineOverview = query(windowSchema, async ({ from, to }) => {
  const { apiClient } = getRequestEvent().locals;
  const [history, glucose] = await Promise.all([
    readable(() => apiClient.profileHistory.getProfileHistory(from, to)),
    readable(() => apiClient.profileHistory.getGlucoseTrace(from, to, 480)),
  ]);
  return { history, glucose };
});

/** Everything the stage draws for its window. */
export const getTimelineStage = query(windowSchema, async ({ from, to }) => {
  const { apiClient } = getRequestEvent().locals;
  const fromIso = new Date(from).toISOString();
  const toIso = new Date(to).toISOString();

  const [lanes, glucose, basalDelivery, pumpModes, overrides, activities, sleep, deviceEvents] =
    await Promise.all([
      readable(() => apiClient.profileHistory.getProfileLanes(from, to)),
      readable(() => apiClient.profileHistory.getGlucoseTrace(from, to, 480)),
      readable(() => apiClient.chartData.getBasalDeliverySpans(from, to)),
      readable(() => apiClient.stateSpans.getPumpModes(fromIso, toIso)),
      readable(() => apiClient.stateSpans.getOverrides(fromIso, toIso)),
      readable(() => apiClient.stateSpans.getActivities(fromIso, toIso)),
      readable(() => apiClient.sleep.getSessions(fromIso, toIso, undefined, undefined, 500)),
      readable(() => apiClient.deviceEvent.getAll(fromIso, toIso, 1000)),
    ]);

  return {
    lanes,
    glucose,
    basalDelivery,
    pumpModes: pumpModes
      ? clip(pumpModes.data, from, to, (s) => s.state ?? "", (s) => PUMP_MODE_COLORS[s.state ?? ""] ?? "var(--muted-foreground)")
      : null,
    overrides: overrides
      ? clip(overrides.data, from, to, (s) => String(s.metadata?.reasonDisplay ?? s.metadata?.reason ?? s.state ?? ""), () => "var(--chart-2)")
      : null,
    activities: activities
      ? clip(
          activities.data,
          from,
          to,
          (s) => s.state ?? "",
          (s) => (s.category && ACTIVITY_COLORS[s.category]) || "var(--muted-foreground)"
        )
      : null,
    sleep: sleep
      ? (sleep.data ?? [])
          .filter((s: SleepSession) => (s.endMills ?? to) > from && (s.startMills ?? 0) < to)
          .map((s: SleepSession, i: number): LaneSpan => ({
            id: s.id ?? `sleep-${s.startMills}-${i}`,
            label: "",
            startMills: Math.max(s.startMills ?? 0, from),
            endMills: Math.min(s.endMills ?? to, to),
            color: "var(--pump-mode-sleep)",
          }))
      : null,
    deviceEvents: deviceEvents
      ? (deviceEvents.data ?? [])
          .filter((e: DeviceEvent) => e.eventType != null && LIFECYCLE_EVENTS.has(e.eventType) && e.mills != null)
          .map((e: DeviceEvent, i: number): LaneMarker => ({
            id: e.id ?? `event-${e.mills}-${i}`,
            mills: e.mills!,
            eventType: e.eventType!,
          }))
      : null,
  };
});

/** The glucose of seven days either side of a change, or null when the caller may not read glucose. */
export const getChangeGlucose = query(z.object({ at: z.number() }), async ({ at }) => {
  const { apiClient } = getRequestEvent().locals;
  return readable(() => apiClient.profileHistory.getGlucoseAroundChange(at, 7));
});
