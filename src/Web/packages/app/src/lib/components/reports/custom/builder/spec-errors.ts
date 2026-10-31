import type { CustomReportSpecError } from "#lib/api/generated/nocturne-api-client.js";
import type { FieldMessage } from "./schema-form.js";

/** The parts of a panel card a validator message can name. */
export type PanelPart = "dataset" | "parameters" | "primitive" | "bindings" | "range" | "title" | "panel";

/** A panel's messages by the part of its card that shows them; `parameters` keep their path inside the dataset. */
export interface PanelMessages {
  dataset: string[];
  parameters: FieldMessage[];
  primitive: string[];
  bindings: FieldMessage[];
  range: string[];
  title: string[];
  panel: string[];
}

export interface SpecMessages {
  /** Messages about the report itself: its title, range, or the list of panels. */
  report: CustomReportSpecError[];
  panels: Map<number, PanelMessages>;
}

/** `panels[2].dataset.name` as 2 and `dataset.name`; null for a path outside the panels. */
function panelPath(path: string): { index: number; rest: string } | null {
  if (!path.startsWith("panels[")) return null;
  const close = path.indexOf("]");
  const index = Number(path.slice("panels[".length, close));
  if (close < 0 || !Number.isInteger(index)) return null;
  const rest = path.slice(close + 1);
  if (rest !== "" && !rest.startsWith(".")) return null;
  return { index, rest: rest.slice(1) };
}

function emptyPanel(): PanelMessages {
  return { dataset: [], parameters: [], primitive: [], bindings: [], range: [], title: [], panel: [] };
}

/** Routes each of the API's messages to the card and the field its path names. */
export function routeSpecErrors(errors: CustomReportSpecError[] | undefined): SpecMessages {
  const routed: SpecMessages = { report: [], panels: new Map() };
  for (const error of errors ?? []) {
    const path = error.path ?? "";
    const message = error.message ?? "";
    const match = panelPath(path);
    if (!match) {
      routed.report.push(error);
      continue;
    }
    const { index, rest } = match;
    const panel = routed.panels.get(index) ?? emptyPanel();
    routed.panels.set(index, panel);
    if (rest === "dataset" || rest === "dataset.name") panel.dataset.push(message);
    else if (rest.startsWith("dataset.")) panel.parameters.push({ path: rest.slice("dataset.".length), message });
    else if (rest === "primitive") panel.primitive.push(message);
    else if (rest.startsWith("bindings.")) panel.bindings.push({ path: rest.slice("bindings.".length), message });
    else if (rest === "range" || rest.startsWith("range.")) panel.range.push(message);
    else if (rest === "title") panel.title.push(message);
    else panel.panel.push(message);
  }
  return routed;
}
