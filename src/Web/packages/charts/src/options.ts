import type { PredictionDisplayMode } from "./predictions.js";
import type { AreaMode, LineColorMode } from "./view/glucose-gradient-stops.js";

/** How the glucose line draws; each field is optional and defaults as `GlucoseTrack` does. */
export interface GlucoseLineAppearance {
  lineColorMode?: LineColorMode;
  lineColor?: string;
  pointColorMode?: LineColorMode;
  pointColor?: string;
  showPoints?: boolean;
  areaMode?: AreaMode;
  areaOpacity?: number;
}

/** A legend toggle, by the key `LegendState.toggle` takes. */
export type LegendToggle =
  | "basal"
  | "iob"
  | "cob"
  | "bolus"
  | "carbs"
  | "pumpModes"
  | "overrideSpans"
  | "profileSpans"
  | "activitySpans";

export interface PredictionOptions {
  show: boolean;
  displayMode: PredictionDisplayMode;
  /** The viewer's switch for forecasts. */
  enabled: boolean;
}
