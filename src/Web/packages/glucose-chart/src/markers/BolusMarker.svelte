<script lang="ts">
  // Native SVG rather than layerchart marks: each mark registers with the chart
  // on mount and the chart's mark deriveds re-run over every mark, so one
  // component per bolus cost O(N^2).
  interface Props {
    xPos: number;
    yPos: number;
    insulin: number;
    isOverride: boolean;
    treatmentId: string;
    onMarkerClick: (treatmentId: string) => void;
  }

  let { xPos, yPos, insulin, isOverride, treatmentId, onMarkerClick }: Props =
    $props();
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  onclick={() => onMarkerClick(treatmentId)}
  class="cursor-pointer"
>
  {#if isOverride}
    <!-- Triangle for manual override -->
    <polygon
      points="0,12 -8,0 8,0"
      class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
    />
  {:else}
    <!-- Hemisphere (dome shape - curves above baseline) -->
    <path
      d="M -8,0 A 8,8 0 0,0 8,0 Z"
      class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
    />
  {/if}
  <text
    y={-14}
    dy="-0.355em"
    text-anchor="middle"
    class="text-[8px] fill-insulin-bolus font-medium"
  >
    {insulin.toFixed(1)}U
  </text>
</g>
