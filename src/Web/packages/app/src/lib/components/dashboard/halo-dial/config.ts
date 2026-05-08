/**
 * Frontend types and defaults for the Halo Dial. The shape of `HaloDialConfig`
 * is owned by the backend (NSwag-generated `HaloDialConfig`); this module only
 * re-exports the enums for ergonomic component-level use, supplies a
 * `defaultHaloDialConfig()` fallback (server should always populate defaults,
 * this is defense in depth), and adds a discriminated union for the per-element
 * options the editor lets the user tweak.
 */

import {
  HaloDialColorMode,
  HaloDialPredictionCurve,
  HaloDialCenterSubElement,
  HaloDialArcElement,
  HaloDialCornerElement,
  type HaloDialConfig,
} from "$lib/api";

export {
  HaloDialColorMode,
  HaloDialPredictionCurve,
  HaloDialCenterSubElement,
  HaloDialArcElement,
  HaloDialCornerElement,
};

/**
 * Mirrors `HaloDialConfig`'s parameterless C# constructor. Components fall
 * back to this when the server returns null; in normal operation the server
 * authors defaults and this is unreachable.
 */
export function defaultHaloDialConfig(): HaloDialConfig {
  return {
    schemaVersion: 1,
    colorMode: HaloDialColorMode.Discrete,
    historyMinutes: 15,
    predictionMinutes: 45,
    predictionCurve: HaloDialPredictionCurve.Main,
    centerSub: HaloDialCenterSubElement.MinutesAndDelta,
    innerLeftArc: HaloDialArcElement.Cob,
    innerRightArc: HaloDialArcElement.Iob,
    iobMaxUnits: 8.0,
    cobMaxGrams: 80.0,
    corners: {
      tl: [],
      tr: [HaloDialCornerElement.LoopDot],
      bl: [],
      br: [
        HaloDialCornerElement.Direction,
        HaloDialCornerElement.Eventual,
        HaloDialCornerElement.LoopLabel,
      ],
    },
    elementConfig: {},
  };
}

// ---------------------------------------------------------------------------
// Per-element options
// ---------------------------------------------------------------------------

export type BasalRateFormat = "u-per-hour" | "percent" | "both";
export type ReservoirFormat = "units" | "percent" | "time-left";
export type AgeFormat = "days" | "days-hours" | "until-expiry";
export type BatteryFormat = "percent" | "voltage" | "time-left";
export type EventualFormat = "value" | "in-x-value";

export type ElementOptions =
  | { kind: HaloDialCornerElement.BasalRate; format: BasalRateFormat }
  | { kind: HaloDialCornerElement.Reservoir; format: ReservoirFormat }
  | { kind: HaloDialCornerElement.SensorAge; format: AgeFormat }
  | { kind: HaloDialCornerElement.PumpSiteAge; format: AgeFormat }
  | { kind: HaloDialCornerElement.Battery; format: BatteryFormat }
  | { kind: HaloDialCornerElement.LoopLabel }
  | { kind: HaloDialCornerElement.LoopDot }
  | { kind: HaloDialCornerElement.Direction }
  | { kind: HaloDialCornerElement.Eventual; format: EventualFormat };

export const DEFAULT_ELEMENT_OPTIONS: { [K in HaloDialCornerElement]: Extract<ElementOptions, { kind: K }> } = {
  [HaloDialCornerElement.BasalRate]: { kind: HaloDialCornerElement.BasalRate, format: "u-per-hour" },
  [HaloDialCornerElement.Reservoir]: { kind: HaloDialCornerElement.Reservoir, format: "units" },
  [HaloDialCornerElement.SensorAge]: { kind: HaloDialCornerElement.SensorAge, format: "days-hours" },
  [HaloDialCornerElement.PumpSiteAge]: { kind: HaloDialCornerElement.PumpSiteAge, format: "days-hours" },
  [HaloDialCornerElement.Battery]: { kind: HaloDialCornerElement.Battery, format: "percent" },
  [HaloDialCornerElement.LoopLabel]: { kind: HaloDialCornerElement.LoopLabel },
  [HaloDialCornerElement.LoopDot]: { kind: HaloDialCornerElement.LoopDot },
  [HaloDialCornerElement.Direction]: { kind: HaloDialCornerElement.Direction },
  [HaloDialCornerElement.Eventual]: { kind: HaloDialCornerElement.Eventual, format: "value" },
};

export type { HaloDialConfig };
