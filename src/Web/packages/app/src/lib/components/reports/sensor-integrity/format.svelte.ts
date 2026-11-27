/**
 * Presentation helpers for the sensor data-quality report. Copy is factual and non-judgemental:
 * it describes what was detected and why, never whether that is good or bad.
 */
import { ClusterConfidence, type GlucoseCluster } from "#lib/api/index.js";
import { bg, bgLabel, formatList } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";

export function confidenceLabel(c: ClusterConfidence | undefined): string {
  switch (c) {
    case ClusterConfidence.High:
      return "High";
    case ClusterConfidence.Medium:
      return "Medium";
    default:
      return "Low";
  }
}

/**
 * A confidence level's key, which names both its chart texture and its badge
 * variant, so a chart band and its chip always match.
 */
export function confidenceKey(
  c: ClusterConfidence | undefined
): "cluster-high" | "cluster-medium" | "cluster-low" {
  switch (c) {
    case ClusterConfidence.High:
      return "cluster-high";
    case ClusterConfidence.Medium:
      return "cluster-medium";
    default:
      return "cluster-low";
  }
}

/** Plain-English description of why a window was flagged, built from the detector diagnostics. */
export function describeCluster(cluster: GlucoseCluster): string {
  const d = cluster.diagnostics;
  if (!d) return "";
  const parts: string[] = [];
  if (d.peakReversals != null) {
    parts.push(plural(Math.round(d.peakReversals), ["# reversal", "# reversals"]));
  }
  if (d.peakIncoherenceRatio != null) {
    const percent = Math.round(d.peakIncoherenceRatio * 100);
    // @wc-include
    parts.push(`${percent}% incoherent`);
  }
  if (d.spikePromoted && d.maxStep != null) {
    const step = `${bg(d.maxStep)} ${bgLabel()}`;
    // @wc-include
    parts.push(`${step} single-step change`);
  }
  if (d.chainPromoted && d.chainSize != null) {
    // @wc-include
    parts.push(`part of a ${d.chainSize}-window run`);
  }
  return formatList(parts, "unit");
}
