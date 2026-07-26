/**
 * Text rendered for a clock element at runtime.
 *
 * The values arrive already formatted for the viewer's unit preference; this
 * module only assembles them, so it stays pure and testable. The builder's
 * `renderElementValue` (lib/clock-builder/utils.ts) is the separate
 * sample-data preview used while editing a face.
 */

import type { ClockElement } from "$lib/api";

export interface ClockElementValueContext {
  /** Current glucose, already converted and formatted. */
  displayBG: string;
  /** Glucose delta, already converted and formatted (carries its own sign). */
  displayDelta: string;
  /** Unit label for the viewer's preference, e.g. "mg/dL". */
  unitLabel: string;
  /** Compact age of the last reading, e.g. "now" or "7m". */
  age: string;
  /** Current time formatted per the element's 12h/24h setting. */
  time: string;
}

export function renderClockElementValue(
  element: ClockElement,
  ctx: ClockElementValueContext
): string {
  switch (element.type) {
    case "sg":
      return ctx.displayBG;
    case "delta":
      // displayDelta already carries the sign.
      return element.showUnits !== false
        ? `${ctx.displayDelta} ${ctx.unitLabel}`
        : ctx.displayDelta;
    case "age":
      return `${ctx.age} ago`;
    case "time":
      return ctx.time;
    // No runtime source for insulin/carbs on board; an explicit placeholder
    // rather than a number the viewer could act on.
    case "iob":
      return "--U";
    case "cob":
      return "--g";
    // "arrow" and "tracker" are rendered by the template as an icon.
    default:
      return element.type === "text" ? (element.text || "") : "";
  }
}
