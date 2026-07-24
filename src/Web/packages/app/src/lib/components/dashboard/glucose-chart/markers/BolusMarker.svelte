<script lang="ts">
  import { Group, Polygon, Text } from "layerchart";

  interface Props {
    xPos: number;
    yPos: number;
    insulin: number;
    isOverride: boolean;
    /** Backend-categorized bolus type (e.g. "AutomaticBolus", "Smb", "Bolus"). */
    bolusType?: string;
    treatmentId: string;
    onMarkerClick: (treatmentId: string) => void;
  }

  let {
    xPos,
    yPos,
    insulin,
    isOverride,
    bolusType,
    treatmentId,
    onMarkerClick,
  }: Props = $props();

  // Algorithm-delivered doses (SMBs / auto-boluses) render as an outlined dome so
  // they read distinctly from a user-initiated (filled) bolus. Category comes from
  // the backend; the frontend only picks the shape.
  const isAutomatic = $derived(
    bolusType === "AutomaticBolus" || bolusType === "Smb",
  );
</script>

<Group
  x={xPos}
  y={yPos + 0}
  onclick={() => onMarkerClick(treatmentId)}
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
  {:else if isAutomatic}
    <!-- Outlined dome for algorithm-delivered doses (SMB / auto-bolus) -->
    <path
      d="M -8,0 A 8,8 0 0,1 8,0 Z"
      fill="none"
      class="stroke-insulin-bolus opacity-90 hover:opacity-100 transition-opacity"
      stroke-width="1.5"
    />
  {:else}
    <!-- Hemisphere (dome shape - curves above baseline) -->
    <path
      d="M -8,0 A 8,8 0 0,1 8,0 Z"
      class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
    />
  {/if}
  <Text
    y={-14}
    textAnchor="middle"
    class="text-[8px] fill-insulin-bolus font-medium"
  >
    {insulin.toFixed(1)}U
  </Text>
</Group>
