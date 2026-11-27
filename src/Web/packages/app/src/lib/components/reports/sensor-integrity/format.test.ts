import { afterEach, describe, expect, it } from "vitest";
import { ClusterConfidence, type GlucoseCluster } from "#lib/api/index.js";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { confidenceKey, confidenceLabel, describeCluster } from "./format.svelte";

describe("sensor integrity format", () => {
  const units = glucoseUnits.current;
  afterEach(() => {
    glucoseUnits.current = units;
  });

  it("names each confidence, and reads a missing one as low", () => {
    expect(confidenceLabel(ClusterConfidence.High)).toBe("High");
    expect(confidenceLabel(ClusterConfidence.Medium)).toBe("Medium");
    expect(confidenceLabel(undefined)).toBe("Low");
    expect(confidenceKey(ClusterConfidence.High)).toBe("cluster-high");
    expect(confidenceKey(ClusterConfidence.Medium)).toBe("cluster-medium");
    expect(confidenceKey(ClusterConfidence.Low)).toBe("cluster-low");
  });

  it("describes a window from every diagnostic it has, as one unit list", () => {
    glucoseUnits.current = "mg/dl";
    const cluster: GlucoseCluster = {
      diagnostics: {
        peakReversals: 3,
        peakIncoherenceRatio: 0.42,
        spikePromoted: true,
        maxStep: 54,
        chainPromoted: true,
        chainSize: 4,
      },
    };

    expect(describeCluster(cluster)).toBe(
      "3 reversals, 42% incoherent, 54 mg/dL single-step change, part of a 4-window run"
    );
  });

  it("leaves out a diagnostic that did not promote the window", () => {
    const cluster: GlucoseCluster = {
      diagnostics: { peakReversals: 1, spikePromoted: false, maxStep: 54, chainPromoted: false, chainSize: 4 },
    };

    expect(describeCluster(cluster)).toBe("1 reversal");
    expect(describeCluster({})).toBe("");
  });
});
