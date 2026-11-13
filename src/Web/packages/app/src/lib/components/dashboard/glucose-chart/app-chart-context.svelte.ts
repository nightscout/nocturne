import type { GlucoseChartContext } from "@nocturne/charts";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { chartFormat } from "#lib/utils/formatting.js";
import { glucoseChartLabels } from "./chart-labels.svelte";

type Supplied = Pick<GlucoseChartContext, "view" | "layout"> &
  Partial<Pick<GlucoseChartContext, "inspection" | "legend" | "printing" | "glucoseUnits">>;

/**
 * A chart context for a host that draws tracks in its own layerchart chart rather than in the
 * shell: what it supplies, with the app's words, time format and units preference for the rest.
 * Pass `supplied` as getters; it is read live.
 */
export function appChartContext(supplied: Supplied): GlucoseChartContext {
  const labels = $derived(glucoseChartLabels());
  return {
    get view() {
      return supplied.view;
    },
    get layout() {
      return supplied.layout;
    },
    get inspection() {
      return supplied.inspection;
    },
    get legend() {
      return supplied.legend;
    },
    get printing() {
      return supplied.printing ?? false;
    },
    get glucoseUnits() {
      return supplied.glucoseUnits ?? glucoseUnits.current;
    },
    get labels() {
      return labels;
    },
    get format() {
      return chartFormat();
    },
  };
}
