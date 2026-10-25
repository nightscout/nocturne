<!--
  Stands in for layerchart's BarChart, which can't run under vitest-browser-svelte. It renders the
  chart's snippets with the tooltip on the first day, and prints the y domain it was given.
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
    yDomain,
    aboveMarks,
    tooltip,
  }: {
    data: unknown[];
    series: Array<{ key: string }>;
    padding: { right: number };
    yDomain: [number, number];
    aboveMarks?: Snippet<[{ context: Context }]>;
    tooltip?: Snippet<[{ context: Context }]>;
  } = $props();

  const context: Context = $derived({
    yScale: { domain: () => yDomain, range: () => [300, 0] },
    tooltip: { data: data[0] },
  });
</script>

<div data-testid="bar-chart" data-padding-right={padding.right} data-y-domain={yDomain.join(",")}>
  {#each series as s (s.key)}<span>{s.key}</span>{/each}
  {@render aboveMarks?.({ context })}
  {@render tooltip?.({ context })}
</div>
