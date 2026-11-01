import type { CustomReportSpecError } from "#lib/api/generated/nocturne-api-client.js";
import type { FieldMessage } from "./schema-form.js";

/** A layer's messages by the part of its row that shows them; `parameters`, `derived` and `bindings` keep their path inside it. */
export interface LayerMessages {
  dataset: string[];
  parameters: FieldMessage[];
  /** Under `derived`: the op's settings (`minutes`), and each source's (`a.field`, `b.dataset.name`). */
  derived: FieldMessage[];
  mark: string[];
  bindings: FieldMessage[];
  range: string[];
  title: string[];
  /** About the layer as a whole: a third unit on its track, say. */
  layer: string[];
}

export interface TrackMessages {
  title: string[];
  height: string[];
  track: string[];
  layers: Map<number, LayerMessages>;
}

/** A panel's messages by the part of its card that shows them; `parameters` keep their path inside the dataset. */
export interface PanelMessages {
  kind: string[];
  dataset: string[];
  parameters: FieldMessage[];
  bindings: FieldMessage[];
  range: string[];
  title: string[];
  grid: string[];
  /** About the tracks as a list: their number. */
  tracks: string[];
  panel: string[];
  trackMessages: Map<number, TrackMessages>;
}

export interface SpecMessages {
  /** Messages about the report itself: its title, range, or the list of panels. */
  report: CustomReportSpecError[];
  panels: Map<number, PanelMessages>;
}

/** `panels[2].dataset.name` as 2 and `dataset.name` for the prefix `panels`; null for a path outside it. */
function indexed(path: string, prefix: string): { index: number; rest: string } | null {
  if (!path.startsWith(`${prefix}[`)) return null;
  const close = path.indexOf("]");
  const index = Number(path.slice(prefix.length + 1, close));
  if (close < 0 || !Number.isInteger(index)) return null;
  const rest = path.slice(close + 1);
  if (rest !== "" && !rest.startsWith(".")) return null;
  return { index, rest: rest.slice(1) };
}

function emptyPanel(): PanelMessages {
  return { kind: [], dataset: [], parameters: [], bindings: [], range: [], title: [], grid: [], tracks: [], panel: [], trackMessages: new Map() };
}

function emptyTrack(): TrackMessages {
  return { title: [], height: [], track: [], layers: new Map() };
}

function emptyLayer(): LayerMessages {
  return { dataset: [], parameters: [], derived: [], mark: [], bindings: [], range: [], title: [], layer: [] };
}

function routeLayer(layer: LayerMessages, rest: string, message: string) {
  if (rest === "dataset" || rest === "dataset.name") layer.dataset.push(message);
  else if (rest.startsWith("dataset.")) layer.parameters.push({ path: rest.slice("dataset.".length), message });
  else if (rest === "derived") layer.derived.push({ path: "", message });
  else if (rest.startsWith("derived.")) layer.derived.push({ path: rest.slice("derived.".length), message });
  else if (rest === "mark") layer.mark.push(message);
  else if (rest === "bindings") layer.bindings.push({ path: "", message });
  else if (rest.startsWith("bindings.")) layer.bindings.push({ path: rest.slice("bindings.".length), message });
  else if (rest === "range" || rest.startsWith("range.")) layer.range.push(message);
  else if (rest === "title") layer.title.push(message);
  else layer.layer.push(message);
}

function routeTrack(track: TrackMessages, rest: string, message: string) {
  const layer = indexed(rest, "layers");
  if (layer) {
    const messages = track.layers.get(layer.index) ?? emptyLayer();
    track.layers.set(layer.index, messages);
    routeLayer(messages, layer.rest, message);
  } else if (rest === "title") track.title.push(message);
  else if (rest === "height") track.height.push(message);
  else track.track.push(message);
}

/** Routes each of the API's messages to the card, the track, the layer row and the field its path names. */
export function routeSpecErrors(errors: CustomReportSpecError[] | undefined): SpecMessages {
  const routed: SpecMessages = { report: [], panels: new Map() };
  for (const error of errors ?? []) {
    const path = error.path ?? "";
    const message = error.message ?? "";
    const match = indexed(path, "panels");
    if (!match) {
      routed.report.push(error);
      continue;
    }
    const { index, rest } = match;
    const panel = routed.panels.get(index) ?? emptyPanel();
    routed.panels.set(index, panel);
    const track = indexed(rest, "tracks");
    if (track) {
      const messages = panel.trackMessages.get(track.index) ?? emptyTrack();
      panel.trackMessages.set(track.index, messages);
      routeTrack(messages, track.rest, message);
    } else if (rest === "kind") panel.kind.push(message);
    else if (rest === "dataset" || rest === "dataset.name") panel.dataset.push(message);
    else if (rest.startsWith("dataset.")) panel.parameters.push({ path: rest.slice("dataset.".length), message });
    else if (rest.startsWith("bindings.")) panel.bindings.push({ path: rest.slice("bindings.".length), message });
    else if (rest === "range" || rest.startsWith("range.")) panel.range.push(message);
    else if (rest === "title") panel.title.push(message);
    else if (rest === "grid") panel.grid.push(message);
    else if (rest === "tracks") panel.tracks.push(message);
    else panel.panel.push(message);
  }
  return routed;
}

/**
 * The messages under `prefix` (`a`, `b.dataset`) with the prefix taken off their paths, for a form
 * drawn over part of a derived layer.
 */
export function messagesUnder(messages: FieldMessage[], prefix: string): FieldMessage[] {
  return messages
    .filter((m) => m.path === prefix || m.path.startsWith(`${prefix}.`))
    .map((m) => ({ path: m.path === prefix ? "" : m.path.slice(prefix.length + 1), message: m.message }));
}
