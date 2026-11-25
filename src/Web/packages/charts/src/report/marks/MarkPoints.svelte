<!--
  A points layer: each row's fields as dots, and the fit field (a scatter's least-squares line) as a
  line through them.
-->
<script lang="ts">
  import { Circle, Path } from "layerchart";
  import { fieldColor, linePath, pointMarks } from "../layered/layered-layout.js";
  import { boundFields, type ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
  }

  let { layer, x, y, paint }: Props = $props();

  const points = $derived(pointMarks(layer, x, y));
  const fit = $derived(boundFields(layer, layer.bindings.fit ? [layer.bindings.fit] : [])[0]);
</script>

{#each points as point (point.key)}
  <Circle cx={point.cx} cy={point.cy} r={2.5} fill={paint(point.color)} fillOpacity={0.7} stroke="none" data-testid="report-layer-point" />
{/each}
{#if fit}
  <Path pathData={linePath(layer.rows, layer.bindings.x ?? "", fit, x, y)} stroke={paint(fieldColor({ color: null }, fit))} strokeWidth={1.5} fill="none" />
{/if}
