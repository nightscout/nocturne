<!--
  An aggregate series as P10-P90 and P25-P75 bands under a centre line, drawn run by run so a gap
  stays a gap and a sparse run is faded. The comparison range is drawn over them as dashed outlines
  of its P25-P75 band and its centre, since a muted fill under strong bands disappears.
-->
<script lang="ts" generics="S extends object">
  import { Area, Spline } from "layerchart";
  import type { LoopAnalysisAggregate } from "#lib/api/index.js";
  import { patternClass } from "#lib/components/charts/print/chart-print-patterns.js";
  import { aggregateRuns, type AggregatePoint } from "./daily-profile-view";

  interface Props {
    slots: S[];
    comparison?: S[] | null;
    pick: (slot: S) => LoopAnalysisAggregate | undefined;
    /** Display-unit conversion applied to every value before it is drawn. */
    scale?: (value: number) => number;
    outerColor: string;
    innerColor: string;
    lineColor?: string;
    /** Draw the P10-P90 band as well as P25-P75. */
    outer?: boolean;
    /** The centre line: the median, or none where the caller draws its own headline. */
    centre?: "p50" | null;
    /** The comparison's ink; the foreground by default, a series' own colour where several share a panel. */
    comparisonColor?: string;
  }

  let {
    slots,
    comparison = null,
    pick,
    scale = (v) => v,
    outerColor,
    innerColor,
    lineColor = "currentColor",
    outer = true,
    centre = "p50",
    comparisonColor = "var(--foreground)",
  }: Props = $props();

  const runs = $derived(aggregateRuns(slots, pick));
  const comparisonRuns = $derived(comparison ? aggregateRuns(comparison, pick) : []);

  const x = (p: AggregatePoint) => p.slot + 0.5;
  const at = (key: keyof LoopAnalysisAggregate) => (p: AggregatePoint) =>
    scale(Number(p.value[key] ?? 0));

  const EDGES: ("p25" | "p75")[] = ["p25", "p75"];
  const SPARSE_OPACITY = 0.35;
</script>

{#each runs as run, i (`p${i}`)}
  {@const opacity = run.sparse ? SPARSE_OPACITY : 1}
  {#if outer}
    <Area
      data={run.points}
      {x}
      y0={at("p10")}
      y1={at("p90")}
      fill={outerColor}
      fill-opacity={opacity}
      class={patternClass("percentile-outer")}
    />
  {/if}
  <Area
    data={run.points}
    {x}
    y0={at("p25")}
    y1={at("p75")}
    fill={innerColor}
    fill-opacity={opacity}
    class={patternClass("percentile-inner")}
  />
  {#if centre}
    <Spline
      data={run.points}
      {x}
      y={at(centre)}
      stroke={lineColor}
      stroke-width={2}
      stroke-opacity={opacity}
      fill="none"
    />
  {/if}
{/each}

{#each comparisonRuns as run, i (`c${i}`)}
  {#each EDGES as edge (edge)}
    <Spline
      data={run.points}
      {x}
      y={at(edge)}
      stroke={comparisonColor}
      stroke-width={1}
      stroke-opacity={0.55}
      stroke-dasharray="2 3"
      fill="none"
    />
  {/each}
  {#if centre}
    <Spline
      data={run.points}
      {x}
      y={at(centre)}
      stroke={comparisonColor}
      stroke-width={1.5}
      stroke-opacity={0.85}
      stroke-dasharray="5 3"
      fill="none"
    />
  {/if}
{/each}
