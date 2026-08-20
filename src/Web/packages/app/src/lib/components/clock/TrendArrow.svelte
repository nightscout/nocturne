<script lang="ts">
  import { ArrowUp } from "lucide-svelte";
  import {
    directionArrowCount,
    directionGlyph,
    directionRotation,
  } from "@nocturne/ui/glucose";

  interface Props {
    direction: string;
    /** Glyph size in px, already scaled by the caller. */
    size: number;
  }

  let { direction, size }: Props = $props();

  const rotation = $derived(directionRotation(direction));
  const arrowCount = $derived(directionArrowCount(direction));
  const arrowIndices = $derived([...Array(arrowCount).keys()]);
</script>

{#if rotation === null}
  <span class="leading-none" style="font-size: {size}px;">
    {directionGlyph(direction)}
  </span>
{:else}
  {#each arrowIndices as index (index)}
    <ArrowUp
      style="width: {size}px; height: {size}px; transform: rotate({rotation}deg);{index <
      arrowCount - 1
        ? ` margin-right: -${size * 0.3}px;`
        : ''}"
    />
  {/each}
{/if}
