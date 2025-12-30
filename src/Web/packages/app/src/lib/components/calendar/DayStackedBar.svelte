<script lang="ts">
  /**
   * Stacked bar chart for calendar day cells. Height is influenced by insulin
   * dose, width by carbs. The golden ratio (1.618:1) represents the "optimal"
   * carb-to-insulin ratio.
   */

  // Golden ratio constant
  const PHI = 1.618;

  // Min/max dimensions for the bar
  const MIN_WIDTH = 16;
  const MAX_WIDTH = 48;
  const MIN_HEIGHT = 20;
  const MAX_HEIGHT = 56;

  interface Props {
    /** Percentages for each glucose range */
    lowPercent: number;
    inRangePercent: number;
    highPercent: number;
    /** Daily totals */
    totalCarbs: number;
    totalInsulin: number;
    /** Max values for the month (for normalization) */
    maxCarbs: number;
    maxInsulin: number;
    /** Click handler */
    onclick?: () => void;
  }

  let {
    lowPercent,
    inRangePercent,
    highPercent,
    totalCarbs,
    totalInsulin,
    maxCarbs,
    maxInsulin,
    onclick,
  }: Props = $props();

  // Calculate width based on carbs (normalized)
  const barWidth = $derived.by(() => {
    if (maxCarbs <= 0) return MIN_WIDTH;
    const normalized = Math.min(totalCarbs / maxCarbs, 1);
    return MIN_WIDTH + (MAX_WIDTH - MIN_WIDTH) * Math.sqrt(normalized);
  });

  // Calculate height based on insulin (normalized)
  const barHeight = $derived.by(() => {
    if (maxInsulin <= 0) return MIN_HEIGHT;
    const normalized = Math.min(totalInsulin / maxInsulin, 1);
    return MIN_HEIGHT + (MAX_HEIGHT - MIN_HEIGHT) * Math.sqrt(normalized);
  });

  // Calculate the actual ratio vs golden ratio for visual indicator
  const actualRatio = $derived.by(() => {
    if (totalInsulin <= 0) return 0;
    return totalCarbs / totalInsulin;
  });

  // How close to the golden ratio (1.0 = perfect)
  const ratioCloseness = $derived.by(() => {
    if (actualRatio <= 0) return 0;
    // Calculate how close the ratio is to PHI (higher = closer)
    const deviation = Math.abs(actualRatio - PHI) / PHI;
    return Math.max(0, 1 - deviation);
  });

  // Colors from CSS variables
  const GLUCOSE_COLORS = {
    low: "var(--glucose-low)",
    inRange: "var(--glucose-in-range)",
    high: "var(--glucose-high)",
  };

  // Build segments for the stacked bar (bottom to top: low, in-range, high)
  const segments = $derived.by(() => {
    const total = lowPercent + inRangePercent + highPercent;
    if (total <= 0) return [];

    return [
      { percent: lowPercent, color: GLUCOSE_COLORS.low, name: "Low" },
      {
        percent: inRangePercent,
        color: GLUCOSE_COLORS.inRange,
        name: "In Range",
      },
      { percent: highPercent, color: GLUCOSE_COLORS.high, name: "High" },
    ].filter((s) => s.percent > 0);
  });
</script>

<button
  type="button"
  class="relative cursor-pointer hover:scale-110 transition-transform focus:outline-none focus:ring-2 focus:ring-primary rounded group"
  {onclick}
>
  <svg
    width={barWidth}
    height={barHeight}
    viewBox="0 0 {barWidth} {barHeight}"
    class="rounded overflow-hidden"
  >
    <!-- Stacked bar segments (rendered bottom to top) -->
    {#each segments as segment, i}
      {@const prevHeight = segments
        .slice(0, i)
        .reduce((sum, s) => sum + (s.percent / 100) * barHeight, 0)}
      {@const segmentHeight = (segment.percent / 100) * barHeight}
      <rect
        x="0"
        y={barHeight - prevHeight - segmentHeight}
        width={barWidth}
        height={segmentHeight}
        fill={segment.color}
        class="transition-all duration-200"
      />
    {/each}

    <!-- Golden ratio indicator line (shows optimal height for given width) -->
    {#if ratioCloseness > 0.7}
      <!-- Near optimal - subtle golden glow -->
      <rect
        x="0"
        y="0"
        width={barWidth}
        height={barHeight}
        fill="none"
        stroke="oklch(0.75 0.15 85)"
        stroke-width="2"
        rx="2"
        class="opacity-60"
      />
    {/if}
  </svg>

  <!-- Ratio indicator dot (golden when close to PHI) -->
  {#if ratioCloseness > 0.8}
    <div
      class="absolute -top-1 -right-1 w-2 h-2 rounded-full"
      style="background: oklch(0.75 0.15 85);"
      title="Near optimal carb/insulin ratio"
    ></div>
  {/if}
</button>
