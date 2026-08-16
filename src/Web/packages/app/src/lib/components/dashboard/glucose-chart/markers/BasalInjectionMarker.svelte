<script lang="ts">
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

<g transform="translate({xPos}, {lineTop})">
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
</g>

<!-- Icon and label at the top -->
<g transform="translate({xPos}, {lineTop - 2})">
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
</g>

<!-- Tooltip-style hover area -->
<g transform="translate({xPos}, {lineTop})">
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
</g>
