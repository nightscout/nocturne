<!--
  A line layer: each bound field as a line, or one line per value of the series field in its hue. A
  glucose field the API gave no hue takes the report's glucose colouring, an SVG gradient on the page
  and the canvas's own gradient in an image; a step field is drawn as steps.
-->
<script lang="ts">
  import { LinearGradient, Path } from "layerchart";
  import type { GradientStop } from "../../view/glucose-gradient-stops.js";
  import { bandSets, fieldColor, glucoseColoured, linePath } from "../layered/layered-layout.js";
  import { boundFields, type ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
    glucoseStops: GradientStop[] | null;
    /** A canvas's gradient for the glucose stops; on the page the line takes an SVG gradient. */
    gradient?: (stops: GradientStop[], height: number) => string | undefined;
    plotHeight: number;
  }

  let { layer, x, y, paint, glucoseStops, gradient, plotHeight }: Props = $props();

  const xName = $derived(layer.bindings.x ?? "");
  const fields = $derived(boundFields(layer, layer.bindings.y));
  const sets = $derived(layer.bindings.series ? bandSets(layer) : null);
</script>

{#if sets}
  {#each sets as set (set.key)}
    {#each fields as field (field.name)}
      <Path pathData={linePath(set.rows, xName, field, x, y)} stroke={paint(set.line)} strokeWidth={set.comparison ? 1.5 : 2} fill="none" />
    {/each}
  {/each}
{:else}
  {#each fields as field (field.name)}
    {@const path = linePath(layer.rows, xName, field, x, y)}
    {#if glucoseStops && glucoseColoured(layer, field)}
      {#if gradient}
        <Path pathData={path} stroke={gradient(glucoseStops.map(([o, c]): GradientStop => [o, paint(c)]), plotHeight) ?? paint("var(--glucose-in-range)")} strokeWidth={2} fill="none" />
      {:else}
        <LinearGradient stops={glucoseStops} units="userSpaceOnUse" vertical y1="0" y2={String(plotHeight)}>
          {#snippet children({ gradient: stroke })}
            <Path pathData={path} {stroke} strokeWidth={2} fill="none" data-testid="report-layer-line" />
          {/snippet}
        </LinearGradient>
      {/if}
    {:else}
      <Path pathData={path} stroke={paint(fieldColor(layer, field))} strokeWidth={2} fill="none" data-testid="report-layer-line" />
    {/if}
  {/each}
{/if}
