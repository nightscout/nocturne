/**
 * Text rendered for a clock element: the single place a face's values are
 * resolved. Glucose arrives in mg/dL and is converted here, in the face's own
 * units: a caller that formats its own copy can show a unit the saved face will
 * not, and the viewer's preference is never consulted.
 *
 * An empty return means the element has no value to show: the live renderer
 * omits it, the builder substitutes a placeholder so it stays selectable.
 */

import type { ClockElement, ClockSettings } from "$lib/api";
import type { GlucoseUnits } from "$lib/stores/appearance-store.svelte";
import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
import { bg, bgDelta, bgLabel } from "$lib/utils/formatting";
import { formatClockTime } from "./clock-time";
import { readingAgePhrase } from "./staleness";

/**
 * The face's units. A face saved before it carried units reads as mg/dL, as the
 * API defaults it.
 */
function faceUnits(settings: ClockSettings | undefined): GlucoseUnits {
  return settings?.glucoseUnits === "mmol" ? "mmol" : "mg/dl";
}

export function renderClockElementValue(
  element: ClockElement,
  settings: ClockSettings | undefined,
  glucose: ClockGlucoseSource,
  now: Date
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
    // No runtime source for insulin/carbs on board; an explicit placeholder
    // rather than a number the viewer could act on.
    case "iob":
      return "--U";
    case "cob":
      return "--g";
    // "arrow" and "tracker" are rendered by the template as an icon.
    default:
      return element.type === "text" ? element.text || "" : "";
  }
}
