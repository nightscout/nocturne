<!--
  The chart package's shell with the app's words, time format, units preference and print
  state, so a page composing its own tracks passes only the data.
-->
<script lang="ts">
  import type { ComponentProps } from "svelte";
  import { GlucoseChartShell, type ChartDataView } from "@nocturne/charts";
  import {
    glucoseUnits as glucoseUnitsPreference,
    type GlucoseUnits,
  } from "#lib/stores/appearance-store.svelte.js";
  import { chartFormat } from "#lib/utils/formatting.js";
  import { PrintMode } from "#lib/components/charts/print/print-mode.svelte.js";
  import { glucoseChartLabels } from "./chart-labels.svelte";

  type Props = Omit<
    ComponentProps<typeof GlucoseChartShell>,
    "view" | "glucoseUnits" | "labels" | "format" | "printing"
  > & {
    engine: ChartDataView;
    /** Units the chart's glucose values are written in. Unset means the viewer's preference. */
    glucoseUnits?: GlucoseUnits;
  };

  let { engine, glucoseUnits, ...rest }: Props = $props();

  const print = new PrintMode();
</script>

<GlucoseChartShell
  view={engine}
  glucoseUnits={glucoseUnits ?? glucoseUnitsPreference.current}
  labels={glucoseChartLabels()}
  format={chartFormat()}
  printing={print.active}
  {...rest}
/>
