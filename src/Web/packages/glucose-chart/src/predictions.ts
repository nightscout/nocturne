/**
 * oref's predicted glucose curves, as an OpenAPS/AndroidAPS/Trio loop uploads them in
 * devicestatus `predBGs`: one mg/dL value per interval, starting at `start`.
 */
export interface PredictionCurves {
  start: Date;
  /** Minutes between consecutive values. oref uses 5. */
  intervalMinutes?: number;
  curves: Partial<Record<PredictionCurveKey, number[]>>;
}

export type PredictionCurveKey = 'ZT' | 'COB' | 'UAM' | 'IOB';

/**
 * How each curve is drawn. Colours are the theme's --pred-* tokens; the dash patterns
 * tell the curves apart without relying on colour.
 */
export const PREDICTION_CURVES: ReadonlyArray<{
  key: PredictionCurveKey;
  label: string;
  class: string;
  dash: string;
}> = [
  { key: 'ZT', label: 'Zero temp', class: 'stroke-pred-zt', dash: '2 4' },
  { key: 'COB', label: 'Carbs on board', class: 'stroke-pred-cob', dash: '9 4' },
  { key: 'UAM', label: 'Unannounced meal', class: 'stroke-pred-uam', dash: '5 3 1 3' },
  { key: 'IOB', label: 'Insulin on board', class: 'stroke-pred-iob', dash: '6 4' },
];
