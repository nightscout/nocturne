import { getRequestEvent, query } from "$app/server";
import type { SensorGlucose, Bolus, CarbIntake, DeviceStatus } from "$lib/api";
import { z } from "zod";

/**
 * Response structure for point-in-time data
 */
export interface PointInTimeData {
  timestamp: number;
  dateString: string;
  dayOfWeek: string;

  // Glucose data
  glucose: {
    value: number;
    direction?: string;
    delta?: number;
    units: string;
  };

  // IOB/COB from device status or calculated
  iob?: {
    value: number;
    basalIob?: number;
    bolusIob?: number;
    source: string;
  };

  cob?: {
    value: number;
    source: string;
  };

  // Recent treatments near this time
  recentTreatments: {
    carbs?: number;
    insulin?: number;
    bolus?: number;
  };

  // Pump/loop status
  pumpStatus?: {
    reservoir?: number;
    battery?: number;
    status?: string;
  };
}

const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

const pointInTimeSchema = z.object({
  timestamp: z.number(),
});

/**
 * Query function to fetch detailed data for a specific point in time
 */
export const getPointInTimeData = query(pointInTimeSchema, async ({ timestamp }): Promise<PointInTimeData | null> => {
  const event = getRequestEvent();
  if (!event) return null;

  const { apiClient } = event.locals;

  // Create a time window around the requested timestamp (±15 minutes)
  const windowMs = 15 * 60 * 1000;
  const fromMs = timestamp - windowMs;
  const toMs = timestamp + windowMs;

  // Fetch glucose readings, boluses, and carb intakes for the time window
  const [glucoseResponse, bolusResponse, carbResponse] = await Promise.all([
    apiClient.glucose.getSensorGlucose(fromMs, toMs, 10000).catch(() => ({ data: [] as SensorGlucose[] })),
    apiClient.insulin.getBoluses(fromMs, toMs, 10000).catch(() => ({ data: [] as Bolus[] })),
    apiClient.nutrition.getCarbIntakes(fromMs, toMs, 10000).catch(() => ({ data: [] as CarbIntake[] })),
  ]);

  const entries = glucoseResponse.data ?? [];
  const boluses = bolusResponse.data ?? [];
  const carbIntakes = carbResponse.data ?? [];

  // Find the closest entry to the requested timestamp
  const targetEntry = entries.reduce((closest: SensorGlucose | null, entry: SensorGlucose) => {
    const entryTime = entry.mills ?? 0;
    const closestTime = closest?.mills ?? 0;
    const targetDiff = Math.abs(entryTime - timestamp);
    const closestDiff = Math.abs(closestTime - timestamp);
    return targetDiff < closestDiff ? entry : closest;
  }, null);

  if (!targetEntry) {
    return null;
  }

  const entryDate = new Date(targetEntry.mills ?? timestamp);
  const glucoseValue = targetEntry.mgdl ?? 0;

  // Calculate delta from previous reading
  const sortedEntries = entries
    .filter((e: SensorGlucose) => (e.mills ?? 0) < (targetEntry.mills ?? 0))
    .sort((a: SensorGlucose, b: SensorGlucose) => (b.mills ?? 0) - (a.mills ?? 0));
  const previousEntry = sortedEntries[0];
  const delta = previousEntry ? glucoseValue - (previousEntry.mgdl ?? 0) : undefined;

  // Aggregate boluses and carb intakes in the window
  const recentTreatments: PointInTimeData["recentTreatments"] = {};

  for (const bolus of boluses) {
    if (bolus.insulin && bolus.insulin > 0) {
      recentTreatments.insulin = (recentTreatments.insulin ?? 0) + bolus.insulin;
      recentTreatments.bolus = (recentTreatments.bolus ?? 0) + bolus.insulin;
    }
  }

  for (const carb of carbIntakes) {
    if (carb.carbs && carb.carbs > 0) {
      recentTreatments.carbs = (recentTreatments.carbs ?? 0) + carb.carbs;
    }
  }

  // Try to get IOB/COB from device status
  let iob: PointInTimeData["iob"];
  let cob: PointInTimeData["cob"];
  let pumpStatus: PointInTimeData["pumpStatus"];

  try {
    // Fetch device status around this time
    const deviceStatusResult = await apiClient.deviceStatus.getDeviceStatus();
    const deviceStatuses = (deviceStatusResult?.data ?? []) as DeviceStatus[];

    // Find closest device status to the timestamp
    const closestStatus = deviceStatuses.reduce((closest: DeviceStatus | null, status: DeviceStatus) => {
      const statusTime = status.mills ?? 0;
      const closestTime = closest?.mills ?? 0;
      const targetDiff = Math.abs(statusTime - timestamp);
      const closestDiff = Math.abs(closestTime - timestamp);
      // Only use if within 30 minutes
      return targetDiff < closestDiff && targetDiff < 30 * 60 * 1000 ? status : closest;
    }, null);

    if (closestStatus) {
      // Extract IOB from various sources
      if (closestStatus.openaps?.iob) {
        const oapsIob = closestStatus.openaps.iob;
        if (typeof oapsIob === "object" && "iob" in oapsIob) {
          iob = {
            value: oapsIob.iob ?? 0,
            basalIob: oapsIob.basaliob,
            bolusIob: oapsIob.bolusiob,
            source: "OpenAPS",
          };
        }
      } else if (closestStatus.loop?.iob) {
        iob = {
          value: closestStatus.loop.iob.iob ?? 0,
          source: "Loop",
        };
      } else if (closestStatus.pump?.iob) {
        iob = {
          value: closestStatus.pump.iob.iob ?? closestStatus.pump.iob.bolusiob ?? 0,
          basalIob: closestStatus.pump.iob.basaliob,
          bolusIob: closestStatus.pump.iob.bolusiob,
          source: "Pump",
        };
      }

      // Extract COB
      if (closestStatus.openaps?.cob !== undefined) {
        cob = {
          value: closestStatus.openaps.cob,
          source: "OpenAPS",
        };
      } else if (closestStatus.loop?.cob) {
        cob = {
          value: closestStatus.loop.cob.cob ?? 0,
          source: "Loop",
        };
      }

      // Extract pump status
      if (closestStatus.pump) {
        pumpStatus = {
          reservoir: closestStatus.pump.reservoir,
          battery: closestStatus.pump.battery?.percent,
          status: closestStatus.pump.status?.status,
        };
      }
    }
  } catch {
    // Device status not available, continue without it
  }

  return {
    timestamp: targetEntry.mills ?? timestamp,
    dateString: entryDate.toLocaleString(),
    dayOfWeek: DAY_NAMES[entryDate.getDay()],
    glucose: {
      value: glucoseValue,
      direction: targetEntry.direction,
      delta,
      units: "mg/dL",
    },
    iob,
    cob,
    recentTreatments,
    pumpStatus,
  };
});
