<script lang="ts">
  import {
    CARB_LABEL_Y,
    CARB_MARKER_POINTS,
    MARKER_HALF_WIDTH,
  } from "../icons/marker-shapes.js";
  import type { TreatmentBubble } from "../view/nightscout-sizing.js";
  import { getGlucoseChartContext } from "../context.svelte.js";

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
    /** Laid out for paper: the labels print larger and in ink. */
    printed?: boolean;
    /**
     * "glyph": a triangle rising above the baseline. "bubble": the upper half of a
     * cgm-remote-monitor treatment pie, its amount above (`bubble` sizes it).
     */
    shape?: "glyph" | "bubble";
    /** Pie geometry for the bubble shape. */
    bubble?: TreatmentBubble;
    /** Outline the lower half too, as Nightscout does for an entry with no insulin. */
    outlineInsulinHalf?: boolean;
  }

  let {
    xPos,
    yPos,
    carbs,
    label,
    treatmentId,
    onMarkerClick,
    showLabel = true,
    printed = false,
    shape = "glyph",
    bubble,
    outlineInsulinHalf = false,
  }: Props = $props();

  const ctx = getGlucoseChartContext();
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
    <path d="M {-r} 0 A {r} {r} 0 0 1 {r} 0 Z" class="fill-carbs" />
    {#if outlineInsulinHalf}
      <path
        d="M {r} 0 A {r} {r} 0 0 1 {-r} 0 Z"
        fill="none"
        class="stroke-insulin-bolus"
        stroke-width="1"
      />
    {/if}
    {#if showLabel}
      <text
        y={-bubble.labelOffset}
        dy="0.35em"
        text-anchor="middle"
        pointer-events="none"
        font-size={bubble.labelSize(carbs)}
        class="bubble-label fill-treatment-label"
      >
        {ctx.labels.carbAmount(String(Math.round(carbs)))}
      </text>
    {/if}
  {:else}
    {#if showLabel}
      <text
        y={CARB_LABEL_Y}
        dy="-0.355em"
        text-anchor="middle"
        pointer-events="none"
        class="{printed ? 'text-2xs fill-foreground' : 'text-3xs fill-entry-carbs'} font-medium"
      >
        {ctx.labels.carbAmount(String(carbs))}
      </text>
    {/if}
    <polygon
      points={CARB_MARKER_POINTS}
      fill="var(--carbs)"
      class="opacity-90 hover:opacity-100 transition-opacity"
    />
  {/if}
  {#if label}
    <text
      x={-((shape === "bubble" && bubble ? bubble.radius : MARKER_HALF_WIDTH) + 3)}
      y={0}
      dy="0.35em"
      text-anchor="end"
      pointer-events="none"
      class="{printed ? 'text-2xs fill-foreground' : 'text-4xs fill-entry-carbs opacity-80'} font-medium"
    >
      {label}
    </text>
  {/if}
</g>

<style>
  /* renderer.js appendLabels: a dark halo keeps the amount legible over the dots. */
  .bubble-label {
    text-shadow: 0 0 10px var(--background);
  }
</style>
