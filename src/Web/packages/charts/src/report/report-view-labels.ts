import type { ChartLabels } from "../labels.js";

/** The words a report view writes: the host's, in its language. Every other word comes from the API. */
export interface ReportViewLabels {
  /** The chart words treatment markers write their amounts with. */
  chart: ChartLabels;
  /** A table or tile panel with no rows. */
  noData: string;
  /** Opens a panel's whole summary under its facts and sentence. */
  fullSummary: string;
  /** How a keyboard reads a chart's values, said when the chart takes focus; the chart takes no focus without it. */
  plotKeys?: string;
}
