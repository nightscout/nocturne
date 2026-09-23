/**
 * Presentation helpers for the sensor data-quality report. Copy is factual and non-judgemental:
 * it describes what was detected and why, never whether that is good or bad.
 */
import { ClusterConfidence, type GlucoseCluster } from "$lib/api";
import { bg, bgLabel } from "$lib/utils/formatting";
import type { TextureKey } from "$lib/components/charts/print/chart-print-patterns";

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

/** Chart texture, and colour, of a confidence level. */
export function confidenceTexture(c: ClusterConfidence | undefined): TextureKey {
  switch (c) {
    case ClusterConfidence.High:
      return "cluster-high";
    case ClusterConfidence.Medium:
      return "cluster-medium";
    default:
      return "cluster-low";
  }
}

/** Tailwind text + background classes for a confidence chip. A tint prints near-white, so print is ink on an outline. */
export function confidenceBadgeVariant(
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
