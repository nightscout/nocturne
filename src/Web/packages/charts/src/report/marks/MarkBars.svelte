<!--
  A bars layer over a continuous axis: each row's fields as bars a slot wide, side by side or stacked,
  each in its field's colour. A bars layer over categories is drawn by CategoryBars.
-->
<script lang="ts">
  import { Rect } from "layerchart";
  import { barRects } from "../layered/layered-layout.js";
  import type { ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
    plotWidth: number;
  }

  let { layer, x, y, paint, plotWidth }: Props = $props();

  const rects = $derived(barRects(layer, x, y, plotWidth));
</script>

{#each rects as rect (rect.key)}
  <Rect x={rect.x} y={rect.y} width={rect.width} height={rect.height} fill={paint(rect.color)} data-testid="report-layer-bar" />
{/each}
