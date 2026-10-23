<!--
  Stands in for the layerchart marks, axis and tooltip parts that need a live chart: it prints
  what each part was given, so a test can read the y values, the axis top and the tooltip rows.
-->
<script lang="ts">
  import type { Snippet } from "svelte";

  let {
    data,
    y,
    scale,
    label,
    value,
    context,
    children,
  }: {
    data?: unknown[];
    y?: (d: unknown) => number;
    scale?: { domain: () => number[] };
    label?: string;
    value?: string;
    context?: { tooltip: { data: unknown } };
    children?: Snippet<[{ data: unknown }]>;
  } = $props();
</script>

{#if data && y}
  <span data-testid="mark">{data.map((d) => y(d)).join(",")}</span>
{:else if scale}
  <span data-testid="axis">{label} {scale.domain()[1]}</span>
{:else if label != null}
  <span>{label}: {value}</span>
{:else if value != null}
  <span>{value}</span>
{/if}
{@render children?.({ data: context?.tooltip.data })}
