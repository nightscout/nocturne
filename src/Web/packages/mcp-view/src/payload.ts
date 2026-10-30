import { z } from "zod";

/** A range as the report spec sends it. */
export type SpecRange = { hours: number } | { from: string; to: string };

/** One event cohort as the spec carries it; the view sends it back as it came. */
export interface SpecCohort {
  anchor: string;
  [key: string]: unknown;
}

/** A refine_report patch: a new range for every panel, or per panel a range or dataset parameters. */
export interface ReportPatch {
  range?: SpecRange;
  panels?: { id: string; range?: SpecRange; dataset?: Record<string, unknown> }[];
}

const specPanels = z.object({
  panels: z.array(
    z.object({
      id: z.string(),
      dataset: z.object({ cohorts: z.array(z.object({ anchor: z.string() }).passthrough()).nullish() }).passthrough(),
    }).passthrough(),
  ),
});

/** The cohorts an event_response panel of the spec names, as sent; one meal cohort when it names none, as the API reads it. */
export function cohortsOf(spec: unknown, panelId: string): SpecCohort[] {
  const parsed = specPanels.safeParse(spec);
  if (!parsed.success) return [];
  const panel = parsed.data.panels.find((p) => p.id === panelId);
  if (!panel) return [];
  return panel.dataset.cohorts ?? [{ anchor: "Meal" }];
}

/** The range that reaches as far again before the panel's window, to the same end. */
export function earlierRange(window: { from: number; to: number }): SpecRange {
  const span = window.to - window.from;
  return { from: new Date(window.from - span).toISOString(), to: new Date(window.to).toISOString() };
}
