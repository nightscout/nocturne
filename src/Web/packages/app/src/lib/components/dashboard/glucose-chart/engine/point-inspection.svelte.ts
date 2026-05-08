import type {
  SeriesFinders,
  GlucosePoint,
  SeriesPoint,
} from "./chart-data-engine.svelte";
import { type BasalPoint, BasalDeliveryOrigin } from "$lib/api";
import { bg, bgLabel } from "$lib/utils/formatting";

// ===== Types =====

export type InspectionDialog =
  | "picker"
  | "glucose"
  | "delivery"
  | "treatment"
  | null;

export interface InspectionContext {
  iob?: number;
  cob?: number;
  basalRate?: number;
  scheduledBasalRate?: number;
  basalOrigin?: BasalDeliveryOrigin;
  pumpMode?: string;
  overrideState?: string;
  profileName?: string;
  activityStates?: string[];
  isStaleBasal: boolean;
  nearbyBolus?: {
    insulin?: number;
    bolusType?: string;
    treatmentId?: string;
    dataSource?: string;
  };
  nearbyCarbs?: {
    carbs?: number;
    label?: string;
    treatmentId?: string;
    dataSource?: string;
  };
  previousGlucoseValue?: number;
  dataSource?: string;
}

export interface InspectionPickerOption {
  type: "glucose" | "delivery" | "treatment";
  label: string;
  preview: string;
}

export interface PointInspection {
  readonly timestamp: Date | null;
  readonly glucosePoint: { sgv: number; color: string } | null;
  readonly context: InspectionContext | null;
  readonly activeDialog: InspectionDialog;
  readonly pickerOptions: InspectionPickerOption[];
  inspect(
    time: Date,
    glucosePoint: { sgv: number; color: string },
    dataSource?: string,
  ): void;
  inspectFromTrack(time: Date): void;
  selectDialog(type: "glucose" | "delivery" | "treatment"): void;
  navigateTo(type: "glucose" | "delivery" | "treatment"): void;
  close(): void;
}

/** Series data getters needed for context gathering */
export interface InspectionSeriesData {
  iobData: () => SeriesPoint[];
  cobData: () => SeriesPoint[];
  basalData: () => BasalPoint[];
}

// ===== Factory =====

export function createPointInspection(
  finders: SeriesFinders,
  glucoseData: () => GlucosePoint[],
  seriesData: InspectionSeriesData,
): PointInspection {
  let timestamp = $state<Date | null>(null);
  let glucosePoint = $state<{ sgv: number; color: string } | null>(null);
  let context = $state<InspectionContext | null>(null);
  let activeDialog = $state<InspectionDialog>(null);

  const pickerOptions = $derived.by((): InspectionPickerOption[] => {
    if (!context || !glucosePoint) return [];

    const opts: InspectionPickerOption[] = [];

    opts.push({
      type: "glucose",
      label: "Glucose",
      preview: `${bg(glucosePoint.sgv)} ${bgLabel()}`,
    });

    if (context.basalRate != null) {
      const rate = context.basalRate ?? 0;
      const mode = context.pumpMode ?? "Basal";
      opts.push({
        type: "delivery",
        label: "Delivery",
        preview: `${mode}, ${rate.toFixed(2)} U/hr`,
      });
    }

    if (context.nearbyBolus || context.nearbyCarbs) {
      const parts: string[] = [];
      if (context.nearbyBolus?.insulin) {
        parts.push(`${context.nearbyBolus.insulin.toFixed(1)}U`);
      }
      if (context.nearbyCarbs?.carbs) {
        parts.push(`${context.nearbyCarbs.carbs}g`);
      }
      opts.push({
        type: "treatment",
        label: "Treatment",
        preview: parts.join(" + ") || "Treatment",
      });
    }

    return opts;
  });

  function gatherContext(
    time: Date,
    point: { sgv: number; color: string },
    dataSource?: string,
  ): void {
    timestamp = time;
    glucosePoint = point;

    const basal = finders.findBasalValue(
      seriesData.basalData(),
      time,
    ) as BasalPoint | undefined;
    const iobVal = finders.findSeriesValue(seriesData.iobData(), time);
    const cobVal = finders.findSeriesValue(seriesData.cobData(), time);
    const pumpMode = finders.findActivePumpMode(time);
    const override = finders.findActiveOverride(time);
    const profile = finders.findActiveProfile(time);
    const activities = finders.findActiveActivities(time);
    const basalDelivery = finders.findActiveBasalDelivery(time);
    const nearbyBol = finders.findNearbyBolus(time);
    const nearbyCarb = finders.findNearbyCarbs(time);
    const prevPoint = finders.findPreviousGlucose(time);
    const isStale = finders.isStaleBasalTime(time);

    context = {
      iob: iobVal?.value,
      cob: cobVal?.value,
      basalRate: basal?.rate ?? basalDelivery?.rate,
      scheduledBasalRate: basal?.scheduledRate,
      basalOrigin: basal?.origin ?? basalDelivery?.origin,
      pumpMode: pumpMode?.state,
      overrideState: override?.state,
      profileName: profile?.state,
      activityStates: activities?.map((a) => a.state ?? "").filter(Boolean),
      isStaleBasal: isStale,
      nearbyBolus: nearbyBol
        ? {
            insulin: nearbyBol.insulin,
            bolusType: nearbyBol.bolusType,
            treatmentId: nearbyBol.treatmentId,
            dataSource: nearbyBol.dataSource,
          }
        : undefined,
      nearbyCarbs: nearbyCarb
        ? {
            carbs: nearbyCarb.carbs,
            label: nearbyCarb.label,
            treatmentId: nearbyCarb.treatmentId,
            dataSource: nearbyCarb.dataSource,
          }
        : undefined,
      previousGlucoseValue: prevPoint?.sgv,
      dataSource,
    };

    // Determine available contexts and decide which dialog to open
    const hasDelivery = basal != null || basalDelivery != null;
    const hasTreatment = nearbyBol != null || nearbyCarb != null;

    if (!hasDelivery && !hasTreatment) {
      activeDialog = "glucose";
    } else {
      activeDialog = "picker";
    }
  }

  function inspect(
    time: Date,
    point: { sgv: number; color: string },
    dataSource?: string,
  ): void {
    gatherContext(time, point, dataSource);
  }

  function inspectFromTrack(time: Date): void {
    const nearest = finders.findSeriesValue(glucoseData(), time);
    const point = nearest
      ? { sgv: nearest.sgv, color: nearest.color }
      : { sgv: 0, color: "var(--color-muted)" };
    gatherContext(time, point, nearest?.dataSource);
  }

  function selectDialog(type: "glucose" | "delivery" | "treatment"): void {
    activeDialog = type;
  }

  function navigateTo(type: "glucose" | "delivery" | "treatment"): void {
    activeDialog = type;
  }

  function close(): void {
    activeDialog = null;
    timestamp = null;
    glucosePoint = null;
    context = null;
  }

  return {
    get timestamp() {
      return timestamp;
    },
    get glucosePoint() {
      return glucosePoint;
    },
    get context() {
      return context;
    },
    get activeDialog() {
      return activeDialog;
    },
    get pickerOptions() {
      return pickerOptions;
    },
    inspect,
    inspectFromTrack,
    selectDialog,
    navigateTo,
    close,
  };
}
