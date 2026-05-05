<script lang="ts">
  import { Group, Rect, Text } from "layerchart";
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

<!-- Dashed vertical line spanning the chart height -->
<Group x={xPos} y={lineTop}>
  {#each { length: Math.floor(lineHeight / 8) } as _, i (i)}
    {#if i % 2 === 0}
      <Rect
        x={-0.75}
        y={i * 8}
        width={1.5}
        height={4}
        class="fill-indigo-500/60 dark:fill-indigo-400/60"
      />
    {/if}
  {/each}
</Group>

<!-- Icon and label at the top -->
<Group x={xPos} y={lineTop - 2}>
  <!-- Background pill -->
  <Rect
    x={-26}
    y={-9}
    width={52}
    height={18}
    rx="9"
    fill="var(--background)"
    class="stroke-indigo-500 dark:stroke-indigo-400"
    stroke-width="1"
    opacity="0.9"
  />
  <!-- Syringe icon via foreignObject -->
  <foreignObject x="-22" y="-7" width="14" height="14">
    <div class="flex items-center justify-center w-full h-full">
      <Syringe size={10} class="text-indigo-600 dark:text-indigo-400" />
    </div>
  </foreignObject>
  <!-- Units label -->
  <Text
    x={2}
    y={0}
    textAnchor="start"
    class="text-[8px] font-medium"
    fill="var(--color-indigo-600)"
    dy="0.35em"
  >
    {units.toFixed(1)}U
  </Text>
</Group>

<!-- Tooltip-style hover area -->
<Group x={xPos} y={lineTop}>
  <Rect
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
  </Rect>
</Group>
