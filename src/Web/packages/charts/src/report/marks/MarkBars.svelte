<!--
  A bars layer over a continuous axis: each row's fields as bars a slot wide, side by side or stacked,
  each in its field's colour. A bars layer over categories is drawn by CategoryBars.
-->
<script lang="ts">
  import { Rect } from "layerchart";
  import { barRects } from "../layered/layered-layout.js";
  import type { TextureKey } from "../../print/patterns.js";
  import type { ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
    plotWidth: number;
    /** The paint of a texture's ink laid over a mark's own fill; none where the layer cannot draw one (a canvas). */
    texture?: (key: TextureKey) => string | undefined;
  }

  let { layer, x, y, paint, plotWidth, texture }: Props = $props();

  const rects = $derived(barRects(layer, x, y, plotWidth));
</script>

{#each rects as rect (rect.key)}
  {@const ink = rect.texture ? texture?.(rect.texture) : undefined}
  <Rect x={rect.x} y={rect.y} width={rect.width} height={rect.height} fill={paint(rect.color)} data-testid="report-layer-bar" data-texture={ink ? rect.texture : undefined} />
  {#if ink}
    <Rect x={rect.x} y={rect.y} width={rect.width} height={rect.height} fill={ink} class="pointer-events-none" />
  {/if}
{/each}
