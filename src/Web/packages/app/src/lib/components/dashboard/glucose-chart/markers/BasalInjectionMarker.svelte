<script lang="ts">
  import { Group } from "layerchart";
  import { Syringe } from "lucide-svelte";

  interface Props {
    xPos: number;
    lineTop: number;
    lineBottom: number;
    units: number;
    insulinName?: string;
  }

  let { xPos, lineTop, lineBottom, units, insulinName }: Props = $props();

  const lineHeight = $derived(lineBottom - lineTop);
</script>

<!-- Native SVG throughout: layerchart 2.x marks each call registerMark(), and this
     marker renders once per basal injection, so a per-segment Rect loop cost O(N^2).
     A single dashed <line> plus native pill/label/hit-area register nothing. -->
<Group x={xPos} y={lineTop}>
  <!-- Dashed vertical line spanning the chart height -->
  <line
    x1={0}
    y1={0}
    x2={0}
    y2={lineHeight}
    stroke-width={1.5}
    stroke-dasharray="4 4"
    class="stroke-indigo-500/60 dark:stroke-indigo-400/60"
  />
</Group>

<!-- Icon and label at the top -->
<Group x={xPos} y={lineTop - 2}>
  <!-- Background pill -->
  <rect
    x={-26}
    y={-9}
    width={52}
    height={18}
    rx="9"
    fill="var(--background)"
    class="stroke-indigo-500 dark:stroke-indigo-400"
    stroke-width="1"
    opacity={0.9}
  />
  <!-- Syringe icon via foreignObject -->
  <foreignObject x="-22" y="-7" width="14" height="14">
    <div class="flex items-center justify-center w-full h-full">
      <Syringe size={10} class="text-indigo-600 dark:text-indigo-400" />
    </div>
  </foreignObject>
  <!-- Units label -->
  <text
    x={2}
    y={0}
    text-anchor="start"
    class="text-[8px] font-medium"
    fill="var(--color-indigo-600)"
    dy="0.35em"
  >
    {units.toFixed(1)}U
  </text>
</Group>

<!-- Tooltip-style hover area -->
<Group x={xPos} y={lineTop}>
  <rect
    x={-8}
    y={0}
    width={16}
    height={lineHeight}
    class="fill-transparent cursor-default"
  >
    {#if insulinName}
      <title>{units.toFixed(1)}U {insulinName}</title>
    {:else}
      <title>{units.toFixed(1)}U basal injection</title>
    {/if}
  </rect>
</Group>
