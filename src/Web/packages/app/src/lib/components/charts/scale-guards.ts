import type { ScaleBand, ScaleTime } from "d3-scale";

// layerchart types a chart context's scales as its own `AnyScale`, whatever
// scale the chart was given. These recover the d3 scale a call site passed in
// by checking for the methods it is about to call. A chart wired to a
// different scale is a programming error, so they throw.

function isBandScale(scale: unknown): scale is ScaleBand<string> {
  return typeof scale === "function" && "bandwidth" in scale && typeof scale.bandwidth === "function";
}

function isTimeScale(scale: unknown): scale is ScaleTime<number, number> {
  return typeof scale === "function" && "invert" in scale && typeof scale.invert === "function";
}

function isNumericScale(scale: unknown): scale is (value: number) => number {
  return typeof scale === "function";
}

export function bandScale(scale: unknown): ScaleBand<string> {
  if (isBandScale(scale)) return scale;
  throw new TypeError("Expected a band scale");
}

export function timeScale(scale: unknown): ScaleTime<number, number> {
  if (isTimeScale(scale)) return scale;
  throw new TypeError("Expected a time scale");
}

export function numericScale(scale: unknown): (value: number) => number {
  if (isNumericScale(scale)) return scale;
  throw new TypeError("Expected a scale");
}
