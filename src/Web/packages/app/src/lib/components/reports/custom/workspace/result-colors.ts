import {
  cohortBands,
  fieldColor,
  glucoseColoured,
  layerColor,
  valueFields,
  type ReportFact,
  type ReportLayer,
  type ReportResult,
} from "@nocturne/charts/core";
import { signal as signalOf, type SignalId } from "./vocabulary.js";

/**
 * How a swatch shows the colour the chart drew a thing in: one colour, the
 * glucose range colours (a line coloured by range), or none where the chart
 * draws it in more than one (one hue per group, or one per mode).
 */
export type Swatch =
  | { kind: "solid"; color: string }
  | { kind: "glucose" }
  | { kind: "none" };

export function findLayer(
  report: ReportResult | null,
  id: string | undefined
): ReportLayer | undefined {
  if (!report || !id) return undefined;
  for (const panel of report.panels)
    for (const track of panel.tracks ?? [])
      for (const layer of track.layers) if (layer.id === id) return layer;
  return undefined;
}

/** The colour a layer's lead mark is drawn in, as `LayeredChart` resolves it. */
function layerSwatch(layer: ReportLayer): Swatch {
  if (layer.mark === "spans") return { kind: "none" };
  const lead = valueFields(layer)[0];
  if (layer.mark === "line" && lead && glucoseColoured(layer, lead))
    return { kind: "glucose" };
  return { kind: "solid", color: layerColor(layer) };
}

/** The colour a layer of the last run was drawn in, or none before a run. */
export function layerSwatchOf(
  report: ReportResult | null,
  id: string
): Swatch {
  const layer = findLayer(report, id);
  return layer ? layerSwatch(layer) : { kind: "none" };
}

/** The colour one field of a layer of the last run was drawn in (a group's bars), or none before a run. */
export function fieldSwatchOf(
  report: ReportResult | null,
  layerId: string | undefined,
  field: string
): Swatch {
  const layer = findLayer(report, layerId);
  const found = layer?.fields.find((f) => f.name === field);
  return layer && found ? { kind: "solid", color: fieldColor(layer, found) } : { kind: "none" };
}

/**
 * A placed signal's swatch: the colour of its first drawn layer in the last
 * run, or its own token before a run. A signal drawn once per group (events,
 * more than one group of days) has none of its own.
 */
export function signalSwatch(
  report: ReportResult | null,
  id: SignalId,
  layers: string[],
  perGroup: boolean
): Swatch {
  if (perGroup) return { kind: "none" };
  const main = layers.find((l) => !/-(sd|smooth|trend)$/.test(l));
  const layer = findLayer(report, main);
  if (layer && layer.mark !== "markers") return layerSwatch(layer);
  if (layer?.mark === "spans" || main === "spans") return { kind: "none" };
  return { kind: "solid", color: `var(${signalOf(id).color})` };
}

/**
 * The colour of event group `index` (from 0): the cohort hues, or the layer's
 * own hue when the API gave it one.
 */
export function cohortSwatch(
  report: ReportResult | null,
  layerId: string | undefined,
  index: number
): Swatch {
  const layer = findLayer(report, layerId);
  if (layer?.color) return { kind: "solid", color: layerColor(layer) };
  return { kind: "solid", color: cohortBands(index).median };
}

/** The colour of a group of days: its first layer's colour in the last run. */
export function dayGroupSwatch(
  report: ReportResult | null,
  layerId: string | undefined
): Swatch {
  const layer = findLayer(report, layerId);
  return layer ? layerSwatch(layer) : { kind: "none" };
}

/**
 * The API's figure for event group `index`: the first layer of the run that has
 * one fact per cohort.
 */
export function cohortFact(
  report: ReportResult | null,
  layerIds: string[],
  index: number
): ReportFact | undefined {
  for (const id of layerIds) {
    const layer = findLayer(report, id);
    if (
      layer?.facts &&
      layer.cohorts &&
      layer.facts.length === layer.cohorts.length
    )
      return layer.facts[index];
  }
  return undefined;
}
