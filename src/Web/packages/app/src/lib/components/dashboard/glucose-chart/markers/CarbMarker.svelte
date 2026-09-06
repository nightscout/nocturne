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
  }

  let { xPos, yPos, carbs, label, treatmentId, onMarkerClick }: Props =
    $props();
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  onclick={() => onMarkerClick(treatmentId)}
  class="cursor-pointer"
>
  <text
    y={CARB_LABEL_Y}
    dy="-0.355em"
    text-anchor="middle"
    pointer-events="none"
    class="text-[8px] fill-carbs font-medium"
  >
    {carbs}g
  </text>
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
      class="text-[7px] fill-carbs font-medium opacity-80"
    >
      {label}
    </text>
  {/if}
</g>
