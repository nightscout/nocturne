<script lang="ts">
  import {
    trianglePoints,
    MARKER_HALF_WIDTH,
    MARKER_HEIGHT,
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
  <!-- Food/meal label above the marker -->
  {#if label}
    <text
      y={-18}
      dy="-0.355em"
      text-anchor="middle"
      class="text-[7px] fill-carbs font-medium opacity-80"
    >
      {label}
    </text>
  {/if}
  <polygon
    points={trianglePoints("up", MARKER_HALF_WIDTH, MARKER_HEIGHT)}
    fill="var(--carbs)"
    class="opacity-90 hover:opacity-100 transition-opacity"
  />
  <text
    y={18}
    dy="-0.355em"
    text-anchor="middle"
    class="text-[8px] fill-carbs font-medium"
  >
    {carbs}g
  </text>
</g>
