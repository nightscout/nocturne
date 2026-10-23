import { getContext, onDestroy, setContext, type Snippet } from "svelte";

/**
 * Filters a report adds to the reports layout's Filters panel, below the date range. The fields
 * edit a draft the report owns; the panel asks the report to start the draft when it opens, and on
 * Apply or Reset asks for the search params with the report's part written, so that the range and
 * the report's filters change in one navigation and one history entry.
 */
export interface ReportFilterExtension {
  fields: Snippet;
  /** Copy the filters in force into the draft. */
  begin: () => void;
  /** `search` with the draft written in, every other parameter kept. */
  apply: (search: URLSearchParams) => URLSearchParams;
  /** `search` without the report's filters, every other parameter kept. */
  reset: (search: URLSearchParams) => URLSearchParams;
}

/** What a report adds to the reports layout. */
export class ReportSlots {
  filters = $state.raw<ReportFilterExtension | null>(null);
}

const REPORT_SLOTS_KEY = "nocturne:app:report-slots";

/** Call from the reports layout. */
export function createReportSlots(): ReportSlots {
  const slots = new ReportSlots();
  setContext(REPORT_SLOTS_KEY, slots);
  return slots;
}

export function getReportSlots(): ReportSlots | undefined {
  return getContext<ReportSlots | undefined>(REPORT_SLOTS_KEY);
}

/** Adds `extension` to the Filters panel for as long as the calling component is mounted. */
export function useReportFilters(extension: ReportFilterExtension): void {
  const slots = getReportSlots();
  if (!slots) return;
  slots.filters = extension;
  onDestroy(() => {
    if (slots.filters === extension) slots.filters = null;
  });
}
