<!--
  An area layer: each bound field as a line with a fill from zero to it, in the field's colour.
-->
<script lang="ts">
  import { Path } from "layerchart";
  import { areaPath, fieldColor, linePath } from "../layered/layered-layout.js";
  import { boundFields, type ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
  }

  let { layer, x, y, paint }: Props = $props();

  const xName = $derived(layer.bindings.x ?? "");
  const fields = $derived(boundFields(layer, layer.bindings.y));
</script>

{#each fields as field (field.name)}
  {@const color = fieldColor(layer, field)}
  <Path
    pathData={areaPath(layer.rows, xName, 0, field.name, x, y, field.step)}
    fill={paint(`color-mix(in oklab, ${color} 35%, transparent)`)}
    stroke="none"
    data-testid="report-layer-area"
  />
  <Path pathData={linePath(layer.rows, xName, field, x, y)} stroke={paint(color)} strokeWidth={1.5} fill="none" />
{/each}
