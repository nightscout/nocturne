<script lang="ts">
  import { BolusType2 } from "../enums.js";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import {
    bolusMarkerPoints,
    BOLUS_LABEL_Y,
    PRINT_BOLUS_LABEL_Y,
    MARKER_HEIGHT,
    MARKER_HEIGHT_OVERRIDE,
  } from "../icons/marker-shapes.js";
  import { formatDose, type TreatmentBubble } from "../view/nightscout-sizing.js";

  interface Props {
    xPos: number;
    yPos: number;
    insulin: number;
    isOverride: boolean;
    /** Backend-categorized bolus type (e.g. "AutomaticBolus", "Smb", "Bolus"). */
    bolusType?: string;
    treatmentId: string;
    onMarkerClick: (treatmentId: string) => void;
    /**
     * Whether to draw the units label. The track turns it off where the text
     * would collide with a neighbour's; the glyph itself always draws.
     */
    showLabel?: boolean;
    /** Laid out for paper: the label prints larger and in ink. */
    printed?: boolean;
    /**
     * "glyph": a triangle hanging below the baseline. "bubble": the lower half of a
     * cgm-remote-monitor treatment pie, its amount underneath (`bubble` sizes it).
     */
    shape?: "glyph" | "bubble";
    /** Pie geometry for the bubble shape. */
    bubble?: TreatmentBubble;
    /** Outline the upper half too, as Nightscout does for an entry with no carbs. */
    outlineCarbHalf?: boolean;
  }

  let {
    xPos,
    yPos,
    insulin,
    isOverride,
    bolusType,
    treatmentId,
    onMarkerClick,
    showLabel = true,
    printed = false,
    shape = "glyph",
    bubble,
    outlineCarbHalf = false,
  }: Props = $props();

  // Algorithm-delivered doses (SMBs / auto-boluses) render outlined so they read
  // distinctly from a user-initiated (filled) bolus. Category comes from the
  // backend; the frontend only picks the shape.
  //
  // Fill and height are independent: a dose that is both automatic and a manual
  // override draws outlined *and* tall, where the two used to be exclusive
  // branches and the override silhouette won.
  const ctx = getGlucoseChartContext();

  const isAutomatic = $derived(
    bolusType === BolusType2.AutomaticBolus || bolusType === BolusType2.Smb,
  );

  const height = $derived(isOverride ? MARKER_HEIGHT_OVERRIDE : MARKER_HEIGHT);
  const points = $derived(bolusMarkerPoints(height));
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  onclick={() => onMarkerClick(treatmentId)}
  class="cursor-pointer"
>
  {#if shape === "bubble" && bubble}
    {@const r = bubble.radius}
    {#if outlineCarbHalf}
      <path d="M {-r} 0 A {r} {r} 0 0 1 {r} 0 Z" fill="none" class="stroke-carbs" stroke-width="1" />
    {/if}
    <path d="M {r} 0 A {r} {r} 0 0 1 {-r} 0 Z" class="fill-insulin-bolus" />
    {#if showLabel}
      <text
        y={bubble.labelOffset}
        dy="0.35em"
        text-anchor="middle"
        pointer-events="none"
        font-size={bubble.labelSize(insulin)}
        class="bubble-label fill-treatment-label"
      >
        {ctx.labels.insulinAmount(formatDose(insulin))}
      </text>
    {/if}
  {:else}
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
    {#if showLabel}
      <text
        y={printed ? PRINT_BOLUS_LABEL_Y : BOLUS_LABEL_Y}
        dy="-0.355em"
        text-anchor="middle"
        pointer-events="none"
        class="{printed ? 'text-2xs fill-foreground' : 'text-3xs fill-insulin-bolus'} font-medium"
      >
        {ctx.labels.insulinAmount(insulin.toFixed(1))}
      </text>
    {/if}
  {/if}
</g>

<style>
  /* renderer.js appendLabels: a dark halo keeps the amount legible over the dots. */
  .bubble-label {
    text-shadow: 0 0 10px var(--background);
  }
</style>
