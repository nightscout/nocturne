<!--
  A band layer: per set (each value of the series field, or the layer's one set) the outer and inner
  ranges between its edges, and its middle line. A comparison range is drawn as outlines over the
  rest. A glucose middle line of the layer's one set takes the report's glucose colouring.
-->
<script lang="ts">
  import { LinearGradient, Path } from "layerchart";
  import type { GradientStop } from "../../view/glucose-gradient-stops.js";
  import { areaPath, bandSets, glucoseColoured, linePath } from "../layered/layered-layout.js";
  import { boundFields, type ReportLayer } from "../types.js";

  interface Props {
    layer: ReportLayer;
    x: (raw: number) => number;
    y: (value: number) => number;
    paint: (css: string) => string;
    glucoseStops: GradientStop[] | null;
    gradient?: (stops: GradientStop[], height: number) => string | undefined;
    plotHeight: number;
  }

  let { layer, x, y, paint, glucoseStops, gradient, plotHeight }: Props = $props();

  const xName = $derived(layer.bindings.x ?? "");
  const edges = $derived(layer.bindings.edges ?? []);
  const middle = $derived(boundFields(layer, layer.bindings.y)[0]);
  const sets = $derived(bandSets(layer));
  /** The ranges each set shades, outer first: P10 to P90 then P25 to P75 of four edges, or the two edges. */
  const ranges = $derived<[string, string, "outer" | "inner"][]>(
    edges.length === 4
      ? [[edges[0], edges[3], "outer"], [edges[1], edges[2], "inner"]]
      : edges.length === 2
        ? [[edges[0], edges[1], middle ? "inner" : "outer"]]
        : [],
  );
  const step = $derived(layer.fields.some((f) => edges.includes(f.name) && f.step));
  const coloured = $derived(!!middle && !!glucoseStops && sets.length === 1 && glucoseColoured(layer, middle));
</script>

{#each sets as set (set.key)}
  {#each ranges as [low, high, which] (`${low}-${high}`)}
    {#if set.comparison}
      <Path pathData={linePath(set.rows, xName, { name: low, label: low, kind: "number" }, x, y)} stroke={paint(set.line)} strokeWidth={1} fill="none" opacity={0.55} />
      <Path pathData={linePath(set.rows, xName, { name: high, label: high, kind: "number" }, x, y)} stroke={paint(set.line)} strokeWidth={1} fill="none" opacity={0.55} />
    {:else}
      <Path
        pathData={areaPath(set.rows, xName, low, high, x, y, step)}
        fill={paint(which === "outer" ? set.outer : set.inner)}
        stroke="none"
        data-testid="report-layer-band"
      />
    {/if}
  {/each}
  {#if middle}
    {@const path = linePath(set.rows, xName, middle, x, y)}
    {#if coloured && glucoseStops}
      {#if gradient}
        <Path pathData={path} stroke={gradient(glucoseStops.map(([o, c]): GradientStop => [o, paint(c)]), plotHeight) ?? paint(set.line)} strokeWidth={2} fill="none" />
      {:else}
        <LinearGradient stops={glucoseStops} units="userSpaceOnUse" vertical y1="0" y2={String(plotHeight)}>
          {#snippet children({ gradient: stroke })}
            <Path pathData={path} {stroke} strokeWidth={2} fill="none" />
          {/snippet}
        </LinearGradient>
      {/if}
    {:else}
      <Path pathData={path} stroke={paint(set.line)} strokeWidth={set.comparison ? 1.5 : 2} fill="none" opacity={set.comparison ? 0.85 : 1} />
    {/if}
  {/if}
{/each}
