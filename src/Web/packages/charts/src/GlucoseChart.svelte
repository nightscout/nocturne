<!--
  The dashboard glucose chart over data the host already holds: no fetch and no live
  updates. The host that has a live view renders GlucoseChartView instead.
-->
<script lang="ts">
  import type { ComponentProps } from "svelte";
  import GlucoseChartView from "./GlucoseChartView.svelte";
  import { createStaticChartView } from "./view/chart-data-view.svelte.js";
  import type { TransformedChartData } from "./transform.js";
  import type { PredictionData } from "./predictions.js";
  import StaticGlucoseChart from "./static/StaticGlucoseChart.svelte";
  import type { StaticRender } from "./theme/static-theme.js";

  type Props = Omit<ComponentProps<typeof GlucoseChartView>, "view"> & {
    data: TransformedChartData;
    /** The window drawn. */
    xDomain: { from: Date; to: Date };
    /** Unix milliseconds; defaults to the window's end. */
    now?: number;
    /** Forecast curves. They draw when `predictions.show` (the options) is also true. */
    predictionData?: PredictionData | null;
    /** Drawn on a canvas in the theme's colours, with no tooltip or legend, for an image. */
    static?: StaticRender;
  };

  let {
    data,
    xDomain,
    now,
    predictionData,
    static: still,
    plotWidth = $bindable(0),
    plotHeight = $bindable(0),
    ...rest
  }: Props = $props();

  const view = createStaticChartView({
    get data() {
      return data;
    },
    get range() {
      return xDomain;
    },
    get now() {
      return now;
    },
    get predictions() {
      return predictionData;
    },
  });
</script>

{#if still}
  <StaticGlucoseChart
    {still}
    {view}
    glucoseUnits={rest.glucoseUnits}
    labels={rest.labels}
    format={rest.format}
    lineColorMode={rest.appearance?.lineColorMode ?? "continuous"}
  />
{:else}
  <GlucoseChartView {view} bind:plotWidth bind:plotHeight {...rest} />
{/if}
