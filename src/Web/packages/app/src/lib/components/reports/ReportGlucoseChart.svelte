<script lang="ts">
  /**
   * The dashboard's glucose chart over a fixed window, for report pages that show one stretch of
   * time in detail: a night on Compression lows, a day on Erroneous readings.
   */
  import type { Snippet } from "svelte";
  import { createChartDataEngine } from "#lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte.js";
  import GlucoseChartShell from "#lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import ThresholdRules from "#lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte";
  import GlucoseTrack from "#lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import BasalTrack from "#lib/components/dashboard/glucose-chart/tracks/BasalTrack.svelte";
  import IobCobTrack from "#lib/components/dashboard/glucose-chart/tracks/IobCobTrack.svelte";
  import ChartTooltip from "#lib/components/dashboard/glucose-chart/ChartTooltip.svelte";
  import { openDayInReview } from "#lib/components/dashboard/glucose-chart/day-in-review.js";
  import type { LegendState } from "#lib/components/dashboard/glucose-chart/chart-context.svelte.js";
  import { cn } from "#lib/utils/index.js";

  interface Props {
    class?: string;
    from: Date;
    to: Date;
    /** Basal above the glucose track and IOB/COB below it. Without them glucose takes the full height. */
    insulin?: boolean;
    selectionDomain?: [Date, Date] | null;
    onSelectionChange?: (domain: [Date, Date] | null) => void;
    /** Drawn behind the glucose trace. */
    under?: Snippet;
    /** Drawn over the glucose trace. */
    over?: Snippet;
    /** Called once the chart's data has arrived. */
    onDataReady?: () => void;
    /** Draw the readings the sensor marked invalid, outlined on the line. */
    invalidReadings?: boolean;
  }

  let { class: className, from, to, insulin = true, selectionDomain, onSelectionChange, under, over, onDataReady, invalidReadings = false }: Props = $props();

  // The tracks and the tooltip read their visibility from the legend; with none they show
  // everything. This chart has no legend to toggle, so glucose-only is a fixed one.
  const GLUCOSE_ONLY: LegendState = {
    iob: false,
    cob: false,
    basal: false,
    bolus: false,
    carbs: false,
    deviceEvents: false,
    alarms: false,
    scheduledTrackers: false,
    basalInjections: false,
    overrideSpans: false,
    profileSpans: false,
    activitySpans: false,
    pumpModes: false,
    expandedPumpModes: false,
    toggle: () => {},
  };
</script>

{#key from.getTime() + "-" + to.getTime()}
  {@const engine = createChartDataEngine({
    dateRange: { from, to },
    enablePredictions: false,
    onDataReady,
    includeInvalidReadings: invalidReadings,
  })}
  <div class={cn("h-64 w-full", className)}>
    <GlucoseChartShell
      {engine}
      heightClass="h-64"
      legend={insulin ? undefined : GLUCOSE_ONLY}
      {selectionDomain}
      {onSelectionChange}
    >
      {#snippet tracks(_ctx)}
        {@render under?.()}
        <ThresholdRules />
        <GlucoseTrack />
        {#if insulin}
          <BasalTrack />
          <IobCobTrack />
        {/if}
        {@render over?.()}
      {/snippet}
      {#snippet overlays(_ctx)}
        <ChartTooltip onTimeClick={openDayInReview} />
      {/snippet}
    </GlucoseChartShell>
  </div>
{/key}
