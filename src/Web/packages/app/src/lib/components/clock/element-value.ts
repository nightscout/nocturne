/**
 * Text rendered for a clock element — the single place a face's values are
 * resolved. Glucose arrives in mg/dL and is converted here: a caller that
 * formats its own copy can show a unit the saved face will not.
 *
 * An empty return means the element has no value to show: the live renderer
 * omits it, the builder substitutes a placeholder so it stays selectable.
 */

import type { ClockElement } from "$lib/api";
import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
import { bg, bgDelta, bgLabel } from "$lib/utils/formatting";
import { formatClockTime } from "./clock-time";
import { readingAgePhrase } from "./staleness";

export function renderClockElementValue(
  element: ClockElement,
  glucose: ClockGlucoseSource,
  now: Date
): string {
  const { currentBG, lastUpdated } = glucose;
  switch (element.type) {
    case "sg":
      return currentBG === null ? "--" : String(bg(currentBG));
    case "delta": {
      // A delta needs a reading, and a second one to be a delta from.
      if (currentBG === null || glucose.bgDelta === null) return "";
      const delta = bgDelta(glucose.bgDelta);
      return element.showUnits !== false ? `${delta} ${bgLabel()}` : delta;
    }
    case "age":
      return lastUpdated === null
        ? ""
        : readingAgePhrase(lastUpdated, now.getTime());
    case "time":
      return formatClockTime(now, element.format);
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
