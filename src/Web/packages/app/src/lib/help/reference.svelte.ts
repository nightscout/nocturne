import type { GlycemicThresholds } from "$api-clients";

export function glucoseBands(t: GlycemicThresholds) {
  return [
    {
      key: "very-low",
      title: "Very low",
      lower: undefined,
      upper: t.veryLow,
      lowerInclusive: false,
      upperInclusive: false,
    },
    {
      key: "low",
      title: "Low",
      lower: t.veryLow,
      upper: t.low,
      lowerInclusive: true,
      upperInclusive: false,
    },
    {
      key: "in-range",
      title: "In range",
      lower: t.targetBottom,
      upper: t.targetTop,
      lowerInclusive: true,
      upperInclusive: true,
    },
    {
      key: "tight-range",
      title: "Tight range (inside in range)",
      lower: t.tightTargetBottom,
      upper: t.tightTargetTop,
      lowerInclusive: true,
      upperInclusive: true,
    },
    {
      key: "high",
      title: "High",
      lower: t.targetTop,
      upper: t.veryHigh,
      lowerInclusive: false,
      upperInclusive: true,
    },
    {
      key: "very-high",
      title: "Very high",
      lower: t.veryHigh,
      upper: undefined,
      lowerInclusive: false,
      upperInclusive: false,
    },
  ] as const;
}
