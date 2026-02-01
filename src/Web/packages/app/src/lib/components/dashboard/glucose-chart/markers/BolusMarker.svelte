<script lang="ts">
  import { Group, Polygon, Text } from "layerchart";
  import type { Treatment } from "$lib/api";

  interface Props {
    xPos: number;
    yPos: number;
    insulin: number;
    treatment: Treatment;
    onMarkerClick: (treatment: Treatment) => void;
  }

  let { xPos, yPos, insulin, treatment, onMarkerClick }: Props = $props();

  // Determine if this bolus was a manual override of a suggestion
  const suggestedTotal = $derived(
    (treatment.insulinRecommendationForCarbs ?? 0) +
      (treatment.insulinRecommendationForCorrection ?? 0)
  );
  const hasSuggestion = $derived(suggestedTotal > 0);
  const isOverride = $derived(
    hasSuggestion && Math.abs(suggestedTotal - insulin) > 0.05
  );
</script>

<Group
  x={xPos}
  y={yPos + 0}
  onclick={() => onMarkerClick(treatment)}
  class="cursor-pointer"
>
  {#if isOverride}
    <!-- Triangle for manual override -->
    <Polygon
      points={[
        { x: 0, y: 12 },
        { x: -8, y: 0 },
        { x: 8, y: 0 },
      ]}
      class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
    />
  {:else}
    <!-- Hemisphere (dome shape - curves above baseline) -->
    <path
      d="M -8,0 A 8,8 0 0,0 8,0 Z"
      class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
    />
  {/if}
  <Text y={-14} textAnchor="middle" class="text-[8px] fill-insulin-bolus font-medium">
    {insulin.toFixed(1)}U
  </Text>
</Group>
