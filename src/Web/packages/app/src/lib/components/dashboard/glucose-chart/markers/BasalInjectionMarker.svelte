<script lang="ts">
  import { trianglePoints } from "$lib/components/icons/marker-shapes";

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
  <!-- Triangle pointing along the time axis, sized to sit inside the pill -->
  <polygon
    points={trianglePoints("right", 5, 9, -10, 0)}
    class="fill-indigo-600 dark:fill-indigo-400"
  />
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
