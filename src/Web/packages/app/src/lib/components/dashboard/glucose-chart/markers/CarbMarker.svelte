<script lang="ts">
  import { Group, Text } from "layerchart";

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

<Group
  x={xPos}
  y={yPos}
  onclick={() => onMarkerClick(treatmentId)}
  class="cursor-pointer"
>
  <!-- Food/meal label above the marker -->
  {#if label}
    <Text
      y={-18}
      textAnchor="middle"
      class="text-[7px] fill-carbs font-medium opacity-80"
    >
      {label}
    </Text>
  {/if}
  <!-- Hemisphere (bowl shape - curves below baseline) -->
  <path
    d="M -8,0 A 8,8 0 0,1 8,0 Z"
    fill="var(--carbs)"
    class="opacity-90 hover:opacity-100 transition-opacity"
  />
  <Text y={18} textAnchor="middle" class="text-[8px] fill-carbs font-medium">
    {carbs}g
  </Text>
</Group>
