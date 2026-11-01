import {
  getReadingCount,
  getReportsAnalysis,
} from "#lib/api/reports.remote.js";
import type { ReportsParamsReturn } from "./date-params.svelte";
import { contextResource, useResourceContext } from "./resource-context.svelte";

interface RangeSummaryOptions {
  errorTitle?: string;
  /**
   * `separate` registers the count and the analytics with the layout on their
   * own, so one can fail or load while the other shows. `combined` registers
   * them as one unit: loading if either loads, with data only when both have
   * it, and a retry runs both.
   */
  registration: "separate" | "combined";
  /**
   * While false neither query runs and both read as empty and idle: an
   * analytics call the viewer's scopes do not allow would 403 and replace the
   * page with an error.
   */
  enabled?: () => boolean;
}

const withheld = {
  loading: false,
  error: null,
  current: undefined,
  refresh: () => {},
};

/**
 * The reading count and the range analytics a report summarises, as resources
 * registered with the layout's guard. Call from component initialisation.
 */
export function rangeSummaryResources(
  params: ReportsParamsReturn,
  options: RangeSummaryOptions
) {
  const { errorTitle, registration, enabled = () => true } = options;
  const active = registration === "combined" ? () => false : undefined;

  const analysis = contextResource(
    () => (enabled() ? getReportsAnalysis(params.dateRangeInput) : withheld),
    { errorTitle, dateParams: params, active }
  );
  const count = contextResource(
    () => (enabled() ? getReadingCount(params.dateRangeInput) : withheld),
    { errorTitle, active }
  );

  if (registration === "combined") {
    useResourceContext({
      loading: () => analysis.loading || count.loading,
      error: () => analysis.error ?? count.error,
      hasData: () => analysis.current != null && count.current != null,
      errorTitle,
      refetch: () => {
        analysis.refresh();
        count.refresh();
      },
    });
  }

  return { analysis, count };
}
