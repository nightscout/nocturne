<!--
  Stands in for layerchart's BarChart, which can't run under vitest-browser-svelte. It renders the
  chart's snippets against a fixed 0–60 U insulin scale, with the tooltip on the first day.
-->
<script lang="ts">
  import type { Snippet } from "svelte";

  type Context = {
    yScale: { domain: () => number[]; range: () => number[] };
    tooltip: { data: unknown };
  };

  let {
    data,
    series,
    padding,
    aboveMarks,
    tooltip,
  }: {
    data: unknown[];
    series: Array<{ key: string }>;
    padding: { right: number };
    aboveMarks?: Snippet<[{ context: Context }]>;
    tooltip?: Snippet<[{ context: Context }]>;
  } = $props();

  const context: Context = $derived({
    yScale: { domain: () => [0, 60], range: () => [300, 0] },
    tooltip: { data: data[0] },
  });
</script>

<div data-testid="bar-chart" data-padding-right={padding.right}>
  {#each series as s (s.key)}<span>{s.key}</span>{/each}
  {@render aboveMarks?.({ context })}
  {@render tooltip?.({ context })}
</div>
