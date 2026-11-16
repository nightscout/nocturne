import type { ReportViewLabels } from "@nocturne/charts/core";
import { glucoseChartLabels } from "#lib/components/dashboard/glucose-chart/chart-labels.svelte.js";

/** A custom report's words in the viewer's language. Built per call so wuchale reads the current locale. */
export function customReportLabels(): ReportViewLabels {
  return {
    chart: glucoseChartLabels(),
    noData: "No data in this range.",
    fullSummary: "Full summary",
    plotKeys:
      "Use the left and right arrow keys to read the values. Use the up and down arrow keys to go to a different axis. Press Escape to stop.",
  };
}
