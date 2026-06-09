/**
 * Maps the edit dialog's v4 *domain* record (mills-first, with a resolved
 * insulinContext) onto the backend-generated *request* DTO shapes consumed by
 * `createEntry` / `updateEntry`. Those commands validate the result against the
 * generated Zod schemas, which are strict (unknown keys are rejected) and
 * expect an ISO `timestamp` rather than `mills` — so the shaping happens here,
 * once, in one place.
 */
import type { EntryRecord } from "$lib/constants/entry-categories";

const APP = "Nocturne Web";

/** Fields shared by every v4 request DTO. */
function common(data: EntryRecord["data"]) {
  const mills = data.mills ?? Date.now();
  return {
    timestamp: new Date(mills).toISOString(),
    utcOffset:
      typeof data.utcOffset === "number"
        ? data.utcOffset
        : -new Date(mills).getTimezoneOffset(),
    app: APP,
  };
}

/** Build the `{ kind, data }` payload for `createEntry`. */
export function toCreateEntryInput(record: EntryRecord) {
  switch (record.kind) {
    case "bolus": {
      const d = record.data;
      return {
        kind: "bolus" as const,
        data: {
          ...common(d),
          insulin: d.insulin,
          programmed: d.programmed,
          delivered: d.delivered,
          bolusType: d.bolusType ?? undefined,
          duration: d.duration,
          automatic: d.automatic,
          insulinType: d.insulinType || undefined,
          patientInsulinId: d.insulinContext?.patientInsulinId,
          correlationId: d.correlationId,
        },
      };
    }
    case "carbs": {
      const d = record.data;
      return {
        kind: "carbs" as const,
        data: {
          ...common(d),
          carbs: d.carbs,
          carbTime: d.carbTime,
          absorptionTime: d.absorptionTime,
          correlationId: d.correlationId,
        },
      };
    }
    case "bgCheck": {
      const d = record.data;
      return {
        kind: "bgCheck" as const,
        data: {
          ...common(d),
          glucose: d.glucose,
          units: d.units,
          glucoseType: d.glucoseType,
        },
      };
    }
    case "note": {
      const d = record.data;
      return {
        kind: "note" as const,
        data: {
          ...common(d),
          text: d.text || undefined,
          eventType: d.eventType || undefined,
          isAnnouncement: d.isAnnouncement,
        },
      };
    }
    case "deviceEvent": {
      const d = record.data;
      return {
        kind: "deviceEvent" as const,
        data: {
          ...common(d),
          eventType: d.eventType,
          notes: d.notes || undefined,
        },
      };
    }
    case "basalInjection": {
      const d = record.data;
      return {
        kind: "basalInjection" as const,
        data: {
          ...common(d),
          patientInsulinId: d.insulinContext?.patientInsulinId,
          units: d.units,
          notes: d.notes || undefined,
          correlationId: d.correlationId,
        },
      };
    }
  }
}

/**
 * Provenance/dedup metadata to carry across an update. The V4 update endpoints
 * replace the record from the request body, so omitting these would blank out
 * the originating device/source on records that came from a connector.
 */
function preserved(data: EntryRecord["data"]) {
  return {
    device: data.device || undefined,
    dataSource: data.dataSource || undefined,
    syncIdentifier: data.syncIdentifier || undefined,
  };
}

/** Build the `{ kind, id, data }` payload for `updateEntry`. */
export function toUpdateEntryInput(record: EntryRecord) {
  const id = record.data.id ?? "";
  switch (record.kind) {
    case "bolus": {
      const d = record.data;
      // UpdateBolusRequest intentionally omits bolusType (delivery pattern is
      // immutable once recorded), so it is not carried over.
      return {
        kind: "bolus" as const,
        id,
        data: {
          ...common(d),
          ...preserved(d),
          insulin: d.insulin,
          programmed: d.programmed,
          delivered: d.delivered,
          duration: d.duration,
          automatic: d.automatic,
          insulinType: d.insulinType || undefined,
          patientInsulinId: d.insulinContext?.patientInsulinId,
          unabsorbed: d.unabsorbed,
          bolusCalculationId: d.bolusCalculationId,
          apsSnapshotId: d.apsSnapshotId,
          correlationId: d.correlationId,
        },
      };
    }
    case "carbs": {
      const d = record.data;
      return {
        kind: "carbs" as const,
        id,
        data: {
          ...common(d),
          ...preserved(d),
          carbs: d.carbs,
          carbTime: d.carbTime,
          absorptionTime: d.absorptionTime,
          correlationId: d.correlationId,
        },
      };
    }
    case "bgCheck": {
      const d = record.data;
      return {
        kind: "bgCheck" as const,
        id,
        data: {
          ...common(d),
          ...preserved(d),
          glucose: d.glucose,
          units: d.units,
          glucoseType: d.glucoseType,
        },
      };
    }
    case "note": {
      const d = record.data;
      return {
        kind: "note" as const,
        id,
        data: {
          ...common(d),
          ...preserved(d),
          text: d.text || undefined,
          eventType: d.eventType || undefined,
          isAnnouncement: d.isAnnouncement,
        },
      };
    }
    case "deviceEvent": {
      const d = record.data;
      return {
        kind: "deviceEvent" as const,
        id,
        data: {
          ...common(d),
          ...preserved(d),
          eventType: d.eventType,
          notes: d.notes || undefined,
        },
      };
    }
    case "basalInjection": {
      const d = record.data;
      return {
        kind: "basalInjection" as const,
        id,
        data: {
          ...common(d),
          ...preserved(d),
          patientInsulinId: d.insulinContext?.patientInsulinId,
          units: d.units,
          notes: d.notes || undefined,
          correlationId: d.correlationId,
        },
      };
    }
  }
}
