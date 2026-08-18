<script lang="ts">
  import { ArrowUp } from "lucide-svelte";
  import {
    directionGlyph,
    directionRotation,
    isDoubleArrow,
  } from "@nocturne/ui/glucose";

  interface Props {
    direction: string;
    /** Glyph size in px, already scaled by the caller. */
    size: number;
  }

  let { direction, size }: Props = $props();

  const rotation = $derived(directionRotation(direction));
</script>

{#if rotation === null}
  <span class="leading-none" style="font-size: {size}px;">
    {directionGlyph(direction)}
  </span>
{:else}
  {#if isDoubleArrow(direction)}
    <ArrowUp
      style="width: {size}px; height: {size}px; transform: rotate({rotation}deg); margin-right: -{size *
        0.3}px;"
    />
  {/if}
  <ArrowUp
    style="width: {size}px; height: {size}px; transform: rotate({rotation}deg);"
  />
{/if}
