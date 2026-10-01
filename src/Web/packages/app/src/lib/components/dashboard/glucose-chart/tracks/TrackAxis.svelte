<!--
  A track's right-hand value axis. On paper the zero tick is dropped, since it
  sits on the track's edge and collides with the time axis's last label, and
  each tick names its unit, because a printed page has no tooltip to ask.
-->
<script lang="ts">
  import { Axis } from "layerchart";
  import type { ScaleLinear } from "d3-scale";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  interface Props {
    scale: ScaleLinear<number, number>;
    unit: string;
  }

  let { scale, unit }: Props = $props();

  const ctx = getGlucoseChartContext();
  const printTicks = $derived(scale.ticks(2).filter((v) => v > 0));
</script>

{#if ctx.printing}
  <Axis
    placement="right"
    {scale}
    ticks={printTicks}
    format={(v) => `${v} ${unit}`}
    tickLabelProps={{ class: "text-2xs fill-foreground" }}
  />
{:else}
  <Axis placement="right" {scale} ticks={2} tickLabelProps={{ class: "text-2xs fill-muted-foreground" }} />
{/if}
