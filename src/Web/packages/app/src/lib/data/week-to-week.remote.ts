import { getRequestEvent, query } from "$app/server";
import type { Entry, Treatment, DeviceStatus } from "$lib/api";
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
    tempBasal?: {
      rate: number;
      duration: number;
    };
    notes?: string[];
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
  const startTime = new Date(timestamp - windowMs);
  const endTime = new Date(timestamp + windowMs);

  // Fetch entries, treatments, and device status for the time window
  const entriesQuery = JSON.stringify({
    date: {
      $gte: startTime.toISOString(),
      $lte: endTime.toISOString(),
    },
  });
  const treatmentsQuery = JSON.stringify({
    created_at: {
      $gte: startTime.toISOString(),
      $lte: endTime.toISOString(),
    },
  });

  const [entries, treatments] = await Promise.all([
    apiClient.entries.getEntries2(entriesQuery).catch(() => [] as Entry[]),
    apiClient.treatments.getTreatments(undefined, undefined, undefined, treatmentsQuery).catch(() => [] as Treatment[]),
  ]);

  // Find the closest entry to the requested timestamp
  const targetEntry = entries.reduce((closest: Entry | null, entry: Entry) => {
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
  const glucoseValue = targetEntry.sgv ?? targetEntry.mgdl ?? 0;

  // Calculate delta from previous reading
  const sortedEntries = entries
    .filter((e: Entry) => (e.mills ?? 0) < (targetEntry.mills ?? 0))
    .sort((a: Entry, b: Entry) => (b.mills ?? 0) - (a.mills ?? 0));
  const previousEntry = sortedEntries[0];
  const delta = previousEntry ? glucoseValue - (previousEntry.sgv ?? previousEntry.mgdl ?? 0) : undefined;

  // Aggregate treatments in the window
  const recentTreatments: PointInTimeData["recentTreatments"] = {};
  const notes: string[] = [];

  for (const treatment of treatments) {
    if (treatment.carbs) {
      recentTreatments.carbs = (recentTreatments.carbs ?? 0) + treatment.carbs;
    }
    if (treatment.insulin) {
      recentTreatments.insulin = (recentTreatments.insulin ?? 0) + treatment.insulin;
    }
    if (treatment.eventType === "Bolus" && treatment.insulin) {
      recentTreatments.bolus = (recentTreatments.bolus ?? 0) + treatment.insulin;
    }
    if (treatment.eventType === "Temp Basal" && treatment.rate !== undefined) {
      recentTreatments.tempBasal = {
        rate: treatment.rate,
        duration: treatment.duration ?? 0,
      };
    }
    if (treatment.notes) {
      notes.push(treatment.notes);
    }
  }

  if (notes.length > 0) {
    recentTreatments.notes = notes;
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
