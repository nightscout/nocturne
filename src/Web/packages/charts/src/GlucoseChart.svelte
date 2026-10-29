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

  type Props = Omit<ComponentProps<typeof GlucoseChartView>, "view"> & {
    data: TransformedChartData;
    /** The window drawn. */
    xDomain: { from: Date; to: Date };
    /** Unix milliseconds; defaults to the window's end. */
    now?: number;
    /** Forecast curves. They draw when `predictions.show` (the options) is also true. */
    predictionData?: PredictionData | null;
  };

  let {
    data,
    xDomain,
    now,
    predictionData,
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

<GlucoseChartView {view} bind:plotWidth bind:plotHeight {...rest} />
