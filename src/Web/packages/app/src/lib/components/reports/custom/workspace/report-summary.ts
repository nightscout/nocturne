/** What a member compares two versions of a report by (see `DraftConflict.svelte`). */
import type { Report } from "./report.svelte.js";
import { stateText } from "./report-draft.svelte.js";

export interface ReportSummary {
  /** The report's name in the list. */
  name: string;
  title: string;
  /** Each block's title, or what it shows when it has none. */
  blocks: string[];
  /** The whole state as text, to tell two versions apart beyond their titles. */
  state: string;
}

export function summarise(report: Report, name: string): ReportSummary {
  return {
    name,
    title: report.title.trim(),
    blocks: report.blocks.map((b) => report.blockTitle(b).trim() || report.caption(b)),
    state: stateText(report),
  };
}
