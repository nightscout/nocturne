<!--
  A track's name. On screen it sits inside the plot at the track's top-left; on
  paper it moves into the left gutter, level with the track, because inside the
  plot the track's first mark or icon prints over it.
-->
<script lang="ts">
  import { getGlucoseChartContext } from "../chart-context.svelte";

  interface Props {
    label: string;
    top: number;
    bottom: number;
    /** A swim lane: too shallow for the track offset, so centred in smaller type. */
    lane?: boolean;
  }

  let { label, top, bottom, lane = false }: Props = $props();

  const ctx = getGlucoseChartContext();
  const middle = $derived((top + bottom) / 2);
</script>

{#if ctx.printing}
  <text x={-6} y={middle} dy="0.35em" text-anchor="end" class="text-2xs fill-foreground font-medium">
    {label}
  </text>
{:else}
  <text
    x={4}
    y={lane ? middle + 3 : top + 12}
    dy="-0.355em"
    class="{lane ? 'text-4xs' : 'text-3xs'} fill-muted-foreground font-medium uppercase"
  >
    {label}
  </text>
{/if}
