import { serializeChartLabels } from "@nocturne/charts/core";
import { glucoseChartLabels } from "#lib/components/dashboard/glucose-chart/chart-labels.svelte.js";

/** The words a report image draws, in the request's language, as plain data for the render worker. */
export function reportImageWords() {
  const count = "{0}";
  return {
    labels: serializeChartLabels(glucoseChartLabels()),
    noData: "No data in this range.",
    moreRows: `This image does not show ${count} of the figures. The summary has all of them.`,
  };
}
