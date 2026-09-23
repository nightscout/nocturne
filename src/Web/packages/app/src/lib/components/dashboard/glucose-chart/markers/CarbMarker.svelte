<script lang="ts">
  import {
    CARB_LABEL_Y,
    CARB_MARKER_POINTS,
    MARKER_HALF_WIDTH,
  } from "$lib/components/icons/marker-shapes";

  interface Props {
    xPos: number;
    yPos: number;
    carbs: number;
    label: string | null;
    treatmentId: string;
    onMarkerClick: (treatmentId: string) => void;
    /**
     * Whether to draw the grams label. The track turns it off where the text
     * would collide with a neighbour's; the glyph itself always draws. The
     * meal name is governed by `label` alone: pass null to withhold it.
     */
    showLabel?: boolean;
  }

  let {
    xPos,
    yPos,
    carbs,
    label,
    treatmentId,
    onMarkerClick,
    showLabel = true,
  }: Props = $props();
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  onclick={() => onMarkerClick(treatmentId)}
  class="cursor-pointer"
>
  {#if showLabel}
    <text
      y={CARB_LABEL_Y}
      dy="-0.355em"
      text-anchor="middle"
      pointer-events="none"
      class="text-3xs fill-entry-carbs font-medium"
    >
      {carbs}g
    </text>
  {/if}
  <polygon
    points={CARB_MARKER_POINTS}
    fill="var(--carbs)"
    class="opacity-90 hover:opacity-100 transition-opacity"
  />
  {#if label}
    <text
      x={-(MARKER_HALF_WIDTH + 3)}
      y={0}
      dy="0.35em"
      text-anchor="end"
      pointer-events="none"
      class="text-4xs fill-entry-carbs font-medium opacity-80"
    >
      {label}
    </text>
  {/if}
</g>
