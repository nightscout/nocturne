import type { FactSnapshotPoint } from "$api-clients";
import type { ConditionNode } from "./types";

/**
 * Sparse-time-series lookup over the per-tick numeric fact snapshots emitted by
 * <c>AlertReplayService</c>. Mirrors the shape of <see
 * cref="LeafTransitionLog"/> but for decimal values instead of booleans, keyed
 * by the snake_case wire name declared on the matching <c>SensorContext</c>
 * property's <c>[ReplayFact]</c> attribute.
 */
export class FactSnapshotLog {
  private readonly points = new Map<string, FactSnapshotPoint[]>();

  constructor(byKey: Record<string, FactSnapshotPoint[] | undefined>) {
    for (const key of Object.keys(byKey)) {
      const arr = byKey[key];
      if (!arr) continue;
      const sorted = arr
        .filter(
          (p): p is FactSnapshotPoint =>
            p?.atMs !== undefined && p?.value !== undefined
        )
        .slice()
        .sort((a, b) => (a.atMs ?? 0) - (b.atMs ?? 0));
      this.points.set(key, sorted);
    }
  }

  /**
   * Returns the fact's value as of <paramref name="atMs"/>, or <c>undefined</c>
   * when no snapshot is available (the fact wasn't observed in this replay, or
   * the query precedes the first emitted point).
   */
  valueAt(factKey: string, atMs: number): number | undefined {
    const points = this.points.get(factKey);
    if (!points || points.length === 0) return undefined;
    const first = points[0].atMs ?? 0;
    if (atMs < first) return undefined;

    let lo = 0;
    let hi = points.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >>> 1;
      if ((points[mid].atMs ?? 0) <= atMs) lo = mid;
      else hi = mid - 1;
    }
    return points[lo].value;
  }
}

/**
 * Resolves a <see cref="ConditionNode"/> to (factKey, formatter) so the rule
 * sidebar can annotate comparison-style leaves with the fact's current value at
 * the playhead. Returns <c>null</c> for non-numeric leaves
 * (composite/not/sustained wrappers, predicted, trend, time_of_day,
 * glucose_bucket, day_of_week, pump_state, state_span_active, do_not_disturb,
 * alert_state, signal_loss). The fact keys match the snake_case wire names on
 * the backend's <c>[ReplayFact]</c> attributes — keep both sides in sync if you
 * add a new comparison leaf.
 */
export function leafFactBinding(
  node: ConditionNode
): { factKey: string; format: (value: number) => string } | null {
  switch (node.type) {
    case "threshold":
      return {
        factKey: "latest_glucose",
        format: (v) => `${Math.round(v)} mg/dL`,
      };
    case "rate_of_change":
      return {
        factKey: "trend_rate",
        format: (v) => `${v.toFixed(2)} mg/dL/min`,
      };
    case "staleness":
      return { factKey: "staleness_minutes", format: formatMinutes };
    case "iob":
      return { factKey: "iob", format: (v) => `${v.toFixed(2)} U` };
    case "cob":
      return { factKey: "cob", format: (v) => `${v.toFixed(1)} g` };
    case "reservoir":
      return { factKey: "reservoir", format: (v) => `${v.toFixed(1)} U` };
    case "site_age":
      return { factKey: "site_age_hours", format: formatHours };
    case "sensor_age":
      return { factKey: "sensor_age_days", format: (v) => `${v.toFixed(1)}d` };
    case "loop_stale":
      return { factKey: "loop_stale_minutes", format: formatMinutes };
    case "loop_enaction_stale":
      return { factKey: "loop_enaction_stale_minutes", format: formatMinutes };
    case "pump_battery":
      return {
        factKey: "pump_battery_percent",
        format: (v) => `${Math.round(v)}%`,
      };
    case "uploader_battery":
      return {
        factKey: "uploader_battery_percent",
        format: (v) => `${Math.round(v)}%`,
      };
    case "temp_basal":
      // Only the rate metric has a current-value snapshot; percent-of-scheduled
      // would need a separate fact.
      if (node.temp_basal?.metric !== "rate") return null;
      return {
        factKey: "temp_basal_rate",
        format: (v) => `${v.toFixed(2)} U/h`,
      };
    case "sensitivity_ratio":
      return { factKey: "sensitivity_ratio", format: (v) => v.toFixed(2) };
    case "time_since_last_carb":
      return { factKey: "time_since_last_carb_minutes", format: formatMinutes };
    case "time_since_last_bolus":
      return {
        factKey: "time_since_last_bolus_minutes",
        format: formatMinutes,
      };
    default:
      return null;
  }
}

function formatMinutes(v: number): string {
  const m = Math.round(v);
  if (m >= 60 && m % 60 === 0) return `${m / 60}h`;
  if (m >= 60) return `${Math.floor(m / 60)}h ${m % 60}m`;
  return `${m}m`;
}

function formatHours(v: number): string {
  if (v >= 24) return `${(v / 24).toFixed(1)}d`;
  return `${v.toFixed(1)}h`;
}
