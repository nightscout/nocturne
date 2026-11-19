<!--
  An area layer: each bound field as a line with a fill from zero to it, in the field's colour.
-->
<script lang="ts">
  import { Path } from "layerchart";
  import type { TextureKey } from "../../print/patterns.js";
  import { areaPath, fieldColor, fieldTexture, linePath } from "../layered/layered-layout.js";
  import { boundFields, type ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
    /** The paint of a texture's ink laid over a mark's own fill; none where the layer cannot draw one (a canvas). */
    texture?: (key: TextureKey) => string | undefined;
  }

  let { layer, x, y, paint, texture }: Props = $props();

  const xName = $derived(layer.bindings.x ?? "");
  const fields = $derived(boundFields(layer, layer.bindings.y));
</script>

{#each fields as field (field.name)}
  {@const color = fieldColor(layer, field)}
  {@const key = fieldTexture(layer, field)}
  {@const ink = key ? texture?.(key) : undefined}
  {@const fill = areaPath(layer.rows, xName, 0, field.name, x, y, field.step)}
  <Path
    pathData={fill}
    fill={paint(`color-mix(in oklab, ${color} 35%, transparent)`)}
    stroke="none"
    data-testid="report-layer-area"
    data-texture={ink ? key : undefined}
  />
  {#if ink}
    <Path pathData={fill} fill={ink} stroke="none" class="pointer-events-none" />
  {/if}
  <Path pathData={linePath(layer.rows, xName, field, x, y)} stroke={paint(color)} strokeWidth={1.5} fill="none" />
{/each}
