/**
 * Text rendered for a clock element: the single place a face's values are
 * resolved. Glucose arrives in mg/dL and is converted here, in the face's own
 * units: a caller that formats its own copy can show a unit the saved face will
 * not, and the viewer's preference is never consulted.
 *
 * An empty return means the element has no value to show: the live renderer
 * omits it, the builder substitutes a placeholder so it stays selectable.
 */

import type { ClockElement, ClockFaceData, ClockSettings } from "#lib/api/index.js";
import type { GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import type { ClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";
import { bg, bgDelta, bgLabel } from "#lib/utils/formatting.js";
import { formatClockTime } from "./clock-time";
import { readingAgePhrase } from "./staleness";
import { DEFAULT_ELEMENT_HOURS, DEFAULT_MINUTES_AHEAD, inRangeLabel } from "./clock-face-data.svelte";

/**
 * The face's units. A face saved before it carried units reads as mg/dL, as the
 * API defaults it.
 */
export function faceUnits(settings: ClockSettings | undefined): GlucoseUnits {
  return settings?.glucoseUnits === "mmol" ? "mmol" : "mg/dl";
}

export function renderClockElementValue(
  element: ClockElement,
  settings: ClockSettings | undefined,
  glucose: ClockGlucoseSource,
  now: Date,
  data: ClockFaceData | null = null
): string {
  const { currentBG, lastUpdated } = glucose;
  const units = faceUnits(settings);
  const withUnits = (value: string) =>
    element.showUnits !== false ? `${value} ${bgLabel(units)}` : value;
  switch (element.type) {
    case "sg":
      return currentBG === null
        ? "--"
        : withUnits(String(bg(currentBG, units)));
    case "delta":
      // A delta needs a reading, and a second one to be a delta from.
      if (currentBG === null || glucose.bgDelta === null) return "";
      return withUnits(bgDelta(glucose.bgDelta, true, units));
    case "age":
      return lastUpdated === null
        ? ""
        : readingAgePhrase(lastUpdated, now.getTime());
    case "time":
      return formatClockTime(now, settings?.timeFormat);
    case "iob":
      return data?.iob == null ? "" : `${data.iob}U`;
    case "cob":
      return data?.cob == null ? "" : `${data.cob}g`;
    case "basal":
      return data?.basal?.rate == null ? "" : `${data.basal.rate} U/h`;
    case "forecast": {
      const minutes = element.minutesAhead || DEFAULT_MINUTES_AHEAD;
      const forecast = data?.forecasts?.find((f) => f.minutesAhead === minutes);
      return forecast?.mgdl == null ? "" : withUnits(String(bg(forecast.mgdl, units)));
    }
    case "summary": {
      const hours = element.hours || DEFAULT_ELEMENT_HOURS;
      const summary = data?.summaries?.find((s) => s.hours === hours);
      return !summary?.readings || summary.inRangePercent == null
        ? ""
        : inRangeLabel(summary.inRangePercent);
    }
    // "arrow", "tracker" and "trackers" are rendered by the template.
    default:
      return element.type === "text" ? element.text || "" : "";
  }
}
