import ArrowUp from "@lucide/svelte/icons/arrow-up";
import ArrowUpRight from "@lucide/svelte/icons/arrow-up-right";
import ArrowRight from "@lucide/svelte/icons/arrow-right";
import ArrowDown from "@lucide/svelte/icons/arrow-down";
import ArrowDownRight from "@lucide/svelte/icons/arrow-down-right";
import HelpCircle from "@lucide/svelte/icons/circle-question-mark";
import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
import { canonicalDirection } from "@nocturne/ui/glucose";
import { Direction } from "@nocturne/api-client";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type SvelteComponent = any;

type DirectionInfo = { label: string; icon: SvelteComponent };

/**
 * Shown for every direction no arrow can express: none reported, not computable, or a
 * spelling this build does not know. A trend we do not have must never read as a stable one.
 */
const UNKNOWN_ICON = HelpCircle;

/** Keyed by the canonical direction name {@link canonicalDirection} yields. */
const ICONS: Partial<Record<string, SvelteComponent>> = {
  [Direction.TripleUp]: ArrowUp,
  [Direction.DoubleUp]: ArrowUp,
  [Direction.SingleUp]: ArrowUpRight,
  [Direction.FortyFiveUp]: ArrowUpRight,
  [Direction.Flat]: ArrowRight,
  [Direction.FortyFiveDown]: ArrowDownRight,
  [Direction.SingleDown]: ArrowDownRight,
  [Direction.DoubleDown]: ArrowDown,
  [Direction.TripleDown]: ArrowDown,
  [Direction.RateOutOfRange]: AlertTriangle,
  [Direction.CgmError]: AlertTriangle,
};

/** Built per call so wuchale reads them in the current locale. */
function directionLabels(): Partial<Record<string, string>> {
  return {
    // @wc-include
    [Direction.TripleUp]: "rising extremely fast",
    // @wc-include
    [Direction.DoubleUp]: "rising very fast",
    // @wc-include
    [Direction.SingleUp]: "rising",
    // @wc-include
    [Direction.FortyFiveUp]: "rising slowly",
    // @wc-include
    [Direction.Flat]: "stable",
    // @wc-include
    [Direction.FortyFiveDown]: "falling slowly",
    // @wc-include
    [Direction.SingleDown]: "falling",
    // @wc-include
    [Direction.DoubleDown]: "falling very fast",
    // @wc-include
    [Direction.TripleDown]: "falling extremely fast",
    // @wc-include
    [Direction.RateOutOfRange]: "out of range",
    // @wc-include
    [Direction.CgmError]: "sensor error",
  };
}

/**
 * Get BG trend direction information. v1/v3 responses carry the space-separated Nightscout
 * spellings ("NOT COMPUTABLE"); v4 carries the enum member names. Both resolve to the same
 * entry, via the same fold the glyph and rotation tables use.
 */
export function getDirectionInfo(direction?: Direction | string): DirectionInfo {
  const canonical = canonicalDirection(direction);
  const label = directionLabels()[canonical];
  return label === undefined
    ? {
        // @wc-include
        label: "unknown",
        icon: UNKNOWN_ICON,
      }
    : { label, icon: ICONS[canonical] };
}
