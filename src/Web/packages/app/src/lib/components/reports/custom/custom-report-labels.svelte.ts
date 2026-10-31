import type { ReportViewLabels } from "@nocturne/charts/core";
import { glucoseChartLabels } from "#lib/components/dashboard/glucose-chart/chart-labels.svelte.js";

/** A custom report's words in the viewer's language. Built per call so wuchale reads the current locale. */
export function customReportLabels(): ReportViewLabels {
  return { chart: glucoseChartLabels(), noData: "No data in this range.", fullSummary: "Full summary" };
}

/** The longest `?spec=` value the API reads; a longer link is refused before it is sent. */
export const MAX_ENCODED_SPEC_LENGTH = 6 * 1024;
