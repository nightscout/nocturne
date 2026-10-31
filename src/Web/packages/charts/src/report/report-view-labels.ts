import type { ChartLabels } from "../labels.js";

/** The words a report view writes: the host's, in its language. Every other word comes from the API. */
export interface ReportViewLabels {
  /** The glucose chart's words, for `glucose_chart` panels. */
  chart: ChartLabels;
  /** A table or tile panel with no rows. */
  noData: string;
  /** Opens a panel's whole summary under its facts and sentence. */
  fullSummary: string;
}
