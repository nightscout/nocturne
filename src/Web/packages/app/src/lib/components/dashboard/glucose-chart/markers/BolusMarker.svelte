<script lang="ts">
  import {
    trianglePoints,
    MARKER_HALF_WIDTH,
    MARKER_HEIGHT,
    MARKER_HEIGHT_OVERRIDE,
  } from "$lib/components/icons/marker-shapes";

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

  // Algorithm-delivered doses (SMBs / auto-boluses) render outlined so they read
  // distinctly from a user-initiated (filled) bolus. Category comes from the
  // backend; the frontend only picks the shape.
  //
  // Fill and height are independent: a dose that is both automatic and a manual
  // override draws outlined *and* tall, where the two used to be exclusive
  // branches and the override silhouette won.
  const isAutomatic = $derived(
    bolusType === "AutomaticBolus" || bolusType === "Smb",
  );

  const points = $derived(
    trianglePoints(
      "down",
      MARKER_HALF_WIDTH,
      isOverride ? MARKER_HEIGHT_OVERRIDE : MARKER_HEIGHT,
    ),
  );
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  onclick={() => onMarkerClick(treatmentId)}
  class="cursor-pointer"
>
  {#if isAutomatic}
    <polygon
      {points}
      fill="none"
      class="stroke-insulin-bolus opacity-90 hover:opacity-100 transition-opacity"
      stroke-width="1.5"
    />
  {:else}
    <polygon
      {points}
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
