import type {
  TenantOverviewPoint,
  TenantOverviewThresholds,
} from "#lib/api/generated/nocturne-api-client.js";
import { FALLBACK_GLUCOSE_Y_MAX } from "@nocturne/charts/core";

const Y_HEADROOM = 1.05;

export interface TracePoint {
  mills: number;
  mgdl: number;
}

export function tracePoints(points: TenantOverviewPoint[] | undefined): TracePoint[] {
  return (points ?? []).flatMap((point) =>
    point.timestamp && point.mgdl != null
      ? [{ mills: new Date(point.timestamp).getTime(), mgdl: point.mgdl }]
      : []
  );
}

/** A reading above the shared ceiling would be clipped flat, so the range grows to hold the highest one. */
export function traceThresholds(
  thresholds: TenantOverviewThresholds | undefined,
  points: TracePoint[]
) {
  return {
    veryLow: thresholds?.urgentLow,
    low: thresholds?.low,
    high: thresholds?.high,
    veryHigh: thresholds?.urgentHigh,
    glucoseYMax: Math.max(
      FALLBACK_GLUCOSE_Y_MAX,
      thresholds?.urgentHigh ?? 0,
      ...points.map((point) => point.mgdl * Y_HEADROOM)
    ),
  };
}
