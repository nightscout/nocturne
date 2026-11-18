<script lang="ts">
  import {
    AlertReplayEventKind,
    type AlertRuleSeverity,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { createChartDataEngine } from "#lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte.js";
  import GlucoseChartShell from "#lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import GlucoseTrack from "#lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import BasalTrack from "#lib/components/dashboard/glucose-chart/tracks/BasalTrack.svelte";
  import IobCobTrack from "#lib/components/dashboard/glucose-chart/tracks/IobCobTrack.svelte";
  import ThresholdRules from "#lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte";
  import ChartTooltip from "#lib/components/dashboard/glucose-chart/ChartTooltip.svelte";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import ReplayOverlay from "../ReplayOverlay.svelte";
  import { legendForRules } from "../replaySeries";
  import type { ConditionNode } from "../types";

  interface Props {
    /** When the alert would have started. */
    at: Date;
    kind: AlertReplayEventKind;
    severity: AlertRuleSeverity;
    /** The alert's condition, which decides the series drawn beside glucose. */
    condition: ConditionNode;
  }

  let { at, kind, severity, condition }: Props = $props();

  const HOURS_BEFORE = 3;
  const HOURS_AFTER = 2;
  const HOUR_MS = 60 * 60 * 1000;

  const range = $derived({
    from: new Date(at.getTime() - HOURS_BEFORE * HOUR_MS),
    to: new Date(Math.min(at.getTime() + HOURS_AFTER * HOUR_MS, Date.now())),
  });
  const legend = $derived(legendForRules([condition]));
  const marker = $derived({
    ev: { at: at.toISOString(), severity, kind, ruleId: "draft" },
    tMs: at.getTime(),
  });

  let readyFor = $state<number | null>(null);
</script>

<div class="relative rounded-md border bg-background p-1" data-testid="alert-firing-chart">
  {#key at.getTime()}
    {@const key = at.getTime()}
    {@const engine = createChartDataEngine({
      dateRange: range,
      enablePredictions: false,
      onDataReady: () => (readyFor = key),
    })}
    <GlucoseChartShell
      {engine}
      {legend}
      heightClass="h-[220px]"
      padding={{ left: 40, right: 12, top: 8, bottom: 30 }}
    >
      {#snippet tracks()}
        <BasalTrack />
        <ThresholdRules />
        <GlucoseTrack />
        <IobCobTrack />
        <ReplayOverlay firedMarkers={[marker]} currentDate={at} />
      {/snippet}
      {#snippet overlays()}
        <ChartTooltip />
      {/snippet}
    </GlucoseChartShell>
  {/key}
  {#if readyFor !== at.getTime()}
    <Skeleton class="absolute inset-1 rounded-sm" />
  {/if}
</div>
