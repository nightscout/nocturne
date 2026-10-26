import { PredictionContext, PredictionCurve } from "#lib/api/index.js";
import { plural } from "#lib/utils/plural.js";
import { formatLocale } from "#lib/utils/formatting.js";
import { hoursText } from "./settings-evidence-labels.svelte";

/** Built per call so wuchale reads them in the current locale. */
export function curveLabel(curve: PredictionCurve | undefined): string {
  switch (curve) {
    case PredictionCurve.Iob:
      return "IOB";
    case PredictionCurve.ZeroTemp:
      return "Zero temp";
    case PredictionCurve.Cob:
      return "COB";
    case PredictionCurve.Uam:
      return "UAM";
    default:
      return "Prediction";
  }
}

/** What each curve assumes, as a one-line caption under its chart. */
export function curveCaption(curve: PredictionCurve | undefined): string {
  switch (curve) {
    case PredictionCurve.Iob:
      return "Insulin on board alone, no carbs";
    case PredictionCurve.ZeroTemp:
      return "Insulin on board, with basal stopped from the time of the prediction";
    case PredictionCurve.Cob:
      return "Absorption of announced carbs";
    case PredictionCurve.Uam:
      return "The rise that the loop sees continues, announced or not";
    default:
      return "The one curve that the loop uses for its decisions";
  }
}

export function contextLabel(context: PredictionContext | undefined): string {
  switch (context) {
    case PredictionContext.Adjusted:
      return "Override, temporary target or exercise";
    case PredictionContext.PostMeal:
      return "After a meal";
    case PredictionContext.PostCorrection:
      return "After a correction";
    default:
      return "Fasting";
  }
}

/** @param lookbackMs How far before a prediction its context looks, as the response serves it. */
export function contextCaption(context: PredictionContext | undefined, lookbackMs: number | undefined): string {
  switch (context) {
    case PredictionContext.Adjusted:
      return "An override, temporary target or exercise was active";
    case PredictionContext.PostMeal:
      return `Carbs were entered in the ${hoursText(lookbackMs)} before`;
    case PredictionContext.PostCorrection:
      return `A manual bolus in the ${hoursText(lookbackMs)} before, no carbs`;
    default:
      return "None of the above";
  }
}

export function horizonLabel(minutes: number | undefined): string {
  return plural(minutes ?? 0, ["+# min", "+# min"]);
}

/** The served settings-signal horizons as a list, e.g. "+30 min and +60 min". */
export function horizonList(minutes: number[]): string {
  return new Intl.ListFormat(formatLocale(), { type: "conjunction" }).format(minutes.map(horizonLabel));
}
