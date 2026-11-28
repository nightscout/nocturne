import type { GlucosePredictionResponse } from "@nocturne/api-client";
import type { PredictionData, PredictionPoint } from "@nocturne/charts/core";

/** A prediction response as the chart draws it: each curve's values placed at their own times. */
export function toPredictionData(response: GlucosePredictionResponse): PredictionData {
  const baseTimestamp = response.timestamp
    ? new Date(response.timestamp).getTime()
    : Date.now();
  const intervalMs = (response.intervalMinutes || 5) * 60 * 1000;

  const toPoints = (predictions: number[] | undefined | null): PredictionPoint[] =>
    (predictions ?? []).map((value, index) => ({
      timestamp: baseTimestamp + index * intervalMs,
      value,
    }));

  return {
    timestamp: response.timestamp ? new Date(response.timestamp) : new Date(),
    currentBg: response.currentBg || 0,
    delta: response.delta || 0,
    eventualBg: response.eventualBg || 0,
    iob: response.iob || 0,
    cob: response.cob || 0,
    sensitivityRatio: response.sensitivityRatio ?? null,
    intervalMinutes: response.intervalMinutes || 5,
    curves: {
      main: toPoints(response.predictions?.default),
      iobOnly: toPoints(response.predictions?.iobOnly),
      uam: toPoints(response.predictions?.uam),
      cob: toPoints(response.predictions?.cob),
      zeroTemp: toPoints(response.predictions?.zeroTemp),
    },
  };
}
