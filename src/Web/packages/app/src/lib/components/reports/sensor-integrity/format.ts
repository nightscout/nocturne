/**
 * Presentation helpers for the sensor data-quality report. Copy is factual and non-judgemental:
 * it describes what was detected and why, never whether that is good or bad.
 */
import { ClusterConfidence, type GlucoseCluster } from "$lib/api";
import { bg, bgLabel } from "$lib/utils/formatting";

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

/** Tailwind text + background classes for a confidence chip. */
export function confidenceChipClass(c: ClusterConfidence | undefined): string {
  switch (c) {
    case ClusterConfidence.High:
      return "bg-cluster-high/15 text-cluster-high";
    case ClusterConfidence.Medium:
      return "bg-cluster-medium/15 text-cluster-medium";
    default:
      return "bg-cluster-low/20 text-cluster-low";
  }
}

/** Plain-English description of why a window was flagged, built from the detector diagnostics. */
export function describeCluster(cluster: GlucoseCluster): string {
  const d = cluster.diagnostics;
  if (!d) return "";
  const parts: string[] = [];
  if (d.peakReversals != null) {
    parts.push(`${Math.round(d.peakReversals)} reversals`);
  }
  if (d.peakIncoherenceRatio != null) {
    parts.push(`${Math.round(d.peakIncoherenceRatio * 100)}% incoherent`);
  }
  if (d.spikePromoted && d.maxStep != null) {
    parts.push(`${bg(d.maxStep)} ${bgLabel()} single-step change`);
  }
  if (d.chainPromoted && d.chainSize != null) {
    parts.push(`part of a ${d.chainSize}-window run`);
  }
  return parts.join(", ");
}

export function formatDuration(minutes: number | undefined): string {
  if (minutes == null) return "—";
  const m = Math.round(minutes);
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  const rem = m % 60;
  return rem === 0 ? `${h} h` : `${h} h ${rem} min`;
}
