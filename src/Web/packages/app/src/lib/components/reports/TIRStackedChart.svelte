<!--
  Time in each glucose range as one stacked bar: the charts package's StackedBar with the app's
  words, the clinical band colours and textures, the tight range as an inset of in-range, and the
  consensus and personal range captions under it.
-->
<script lang="ts">
  import { StackedBar, type StackedBarSegment } from "@nocturne/charts";
  import { bgRange } from "#lib/utils/formatting.js";
  import type { TimeInRangePercentages } from "#lib/api/generated/nocturne-api-client.js";

  interface Props {
    /** Pre-computed percentages - required to avoid reactive API calls */
    percentages?: TimeInRangePercentages;
    /** Served consensus target range bounds in mg/dL; the caption below the chart shows when given */
    thresholds?: { low: number; high: number };
    /** Chart orientation - 'vertical' (default) or 'horizontal' */
    orientation?: "vertical" | "horizontal";
    /**
     * Band labels: beside the bar when vertical; when horizontal, a key under the bar on paper
     * only unless keyOnScreen is set (default: true).
     */
    showLabels?: boolean;
    /** Show the horizontal key on screen as well as on paper, so the segments read without hovering (default: false) */
    keyOnScreen?: boolean;
    /** Compact mode - smaller text and tighter spacing */
    compact?: boolean;
    /**
     * Overlay marking where the tenant's personal target range falls, alongside the clinical
     * bands. Unlike tightTarget, a personal range isn't a subset of any single clinical band
     * (it can straddle boundaries), so it's drawn as two markers across the whole bar. Markers
     * are omitted when belowPercent/abovePercent are absent (e.g. time-of-day schedules, where
     * cumulative-time positions don't correspond to glucose boundaries on a value-sorted bar).
     */
    personalRange?: {
      /** % of time below the personal range (0-100); marker drawn at this cumulative offset */
      belowPercent?: number;
      /** % of time above the personal range (0-100); marker drawn at 100 minus this offset */
      abovePercent?: number;
      /** Caption shown under the chart, e.g. "Your range: 80-160 - 74% of time" */
      label: string;
    };
  }

  let {
    percentages,
    thresholds,
    orientation = "vertical",
    showLabels = true,
    keyOnScreen = false,
    compact = false,
    personalRange,
  }: Props = $props();

  const vertical = $derived(orientation === "vertical");

  // Lowest glucose first: bottom to top when vertical, left to right when horizontal.
  const segments = $derived<StackedBarSegment[]>([
    { key: "veryLow", label: "Very low", value: percentages?.veryLow ?? 0, color: "var(--glucose-very-low)", pattern: "very-low" },
    { key: "low", label: "Low", value: percentages?.low ?? 0, color: "var(--glucose-low)", pattern: "low" },
    { key: "target", label: "In range", value: percentages?.target ?? 0, color: "var(--glucose-in-range)", pattern: "in-range" },
    { key: "high", label: "High", value: percentages?.high ?? 0, color: "var(--glucose-high)", pattern: "high" },
    { key: "veryHigh", label: "Very high", value: percentages?.veryHigh ?? 0, color: "var(--glucose-very-high)", pattern: "very-high" },
  ]);

  // Tight range (70-140 mg/dL) is a subset of in-range, not a sibling band.
  const inset = $derived({
    within: "target",
    value: percentages?.tightTarget ?? 0,
    label: "Tight range",
    keyLabel: "in tight range",
    color: "var(--glucose-tight-range)",
    pattern: "tight-range" as const,
  });

  const markers = $derived(
    personalRange?.belowPercent === undefined || personalRange.abovePercent === undefined
      ? null
      : [personalRange.belowPercent, 100 - personalRange.abovePercent]
  );
</script>

<div class={vertical ? "flex h-full w-full flex-col" : "w-full"}>
  <StackedBar
    {segments}
    {orientation}
    {showLabels}
    {keyOnScreen}
    {compact}
    emphasis="target"
    {inset}
    {markers}
  />

  {#if thresholds || personalRange}
    <div class={["mt-2 shrink-0 text-center text-muted-foreground", compact ? "text-2xs" : "text-xs"].join(" ")}>
      {#if thresholds}
        <p><span class="font-semibold text-foreground">Consensus target range:</span> {bgRange(thresholds.low, thresholds.high)}</p>
      {/if}
      {#if personalRange}
        <p>{personalRange.label}</p>
      {/if}
    </div>
  {/if}
</div>
