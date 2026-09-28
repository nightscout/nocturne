<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import { Badge } from "$lib/components/ui/badge";
  import CheckCircle2 from "@lucide/svelte/icons/circle-check";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import Bell from "@lucide/svelte/icons/bell";
  import { Tooltip } from "layerchart";
  import {
    AlertReplayEventKind,
    type AlertReplayResult,
    type AlertReplayEvent,
    type AlertRuleResponse,
  } from "$api-clients";
  import { indexBy } from "$lib/utils/collections";
  import { time } from "$lib/utils/formatting";
  import type { ChartDataEngine } from "$lib/components/dashboard/glucose-chart/engine/chart-data-view.svelte";
  import GlucoseChartShell from "$lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import GlucoseTrack from "$lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte";
  import BasalTrack from "$lib/components/dashboard/glucose-chart/tracks/BasalTrack.svelte";
  import IobCobTrack from "$lib/components/dashboard/glucose-chart/tracks/IobCobTrack.svelte";
  import ThresholdRules from "$lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte";
  import ChartTooltip from "$lib/components/dashboard/glucose-chart/ChartTooltip.svelte";
  import { severityLabel, severityVar } from "./severity";
  import ReplayOverlay from "./ReplayOverlay.svelte";
  import PlaybackStrip from "./PlaybackStrip.svelte";
  import RuleSidebar from "./RuleSidebar.svelte";
  import { LeafTransitionLog, assignLeafIds } from "./leafEval";
  import { FactSnapshotLog } from "./factSnapshot";
  import { legendForRules } from "./replaySeries";
  import { nodeFromApi, ensureCompositeRoot, type ConditionNode } from "./types";

  interface Props {
    result: AlertReplayResult;
    /** The rules the replay ran, for the sidebar. */
    rules: AlertRuleResponse[];
    /** Pinned to the top of the sidebar with an "(editing)" marker. */
    editingRuleId?: string;
    /** Stands in for the saved tree of `editingRuleId`, so unsaved rows show in the sidebar. */
    editingTree?: ConditionNode;
    /** Builds the chart for the replay window; called again when the window changes. */
    chartEngine: (options: {
      range: { from: Date; to: Date };
      onDataReady: () => void;
    }) => ChartDataEngine;
    onSelectionChange?: (domain: [Date, Date] | null) => void;
    /** Draw glucose and only the series the rules read, rather than every series. */
    seriesFromRules?: boolean;
    /** Every rule starts expanded, with no switch to leave it out. */
    fixedRules?: boolean;
    /** Makes the chart's time label a link. */
    onTimeClick?: (time: Date | undefined) => void;
  }

  let {
    result,
    rules,
    editingRuleId,
    editingTree,
    chartEngine,
    onSelectionChange,
    seriesFromRules = false,
    fixedRules = false,
    onTimeClick,
  }: Props = $props();

  // The rule under edit substitutes its in-memory tree so the sidebar reflects
  // the editor rather than the saved version.
  const parsedTrees = $derived(
    rules.flatMap((r) => {
      const parsed =
        editingRuleId && r.id === editingRuleId && editingTree
          ? editingTree
          : nodeFromApi(r.conditionType, r.conditionParams);
      return r.id && parsed ? [{ id: r.id, tree: ensureCompositeRoot(parsed) }] : [];
    })
  );
  const treeByRule = $derived(indexBy(parsedTrees, (t) => t.id, (t) => t.tree));
  const leafIdsByRule = $derived(
    indexBy(parsedTrees, (t) => t.id, (t) => assignLeafIds(t.tree))
  );
  const leafLog = $derived(new LeafTransitionLog(result.leafTransitionsByRule ?? {}));
  const factLog = $derived(new FactSnapshotLog(result.factTimelines ?? {}));
  const legend = $derived(
    seriesFromRules ? legendForRules(parsedTrees.map((t) => t.tree)) : undefined
  );
  let disabledRuleIds = $state<Set<string>>(new Set());

  const xDomain = $derived.by<[Date, Date] | undefined>(() => {
    if (!result.windowStart || !result.windowEnd) return undefined;
    return [new Date(result.windowStart), new Date(result.windowEnd)];
  });
  const windowKey = $derived(
    xDomain ? `${xDomain[0].getTime()}-${xDomain[1].getTime()}` : ""
  );

  type Marker = { ev: AlertReplayEvent; tMs: number };

  // Falls back to the severity label for legacy events that pre-date the discriminator.
  function kindLabel(ev: AlertReplayEvent): string {
    switch (ev.kind) {
      case AlertReplayEventKind.AutoResolved:
        return "Resolved";
      case AlertReplayEventKind.SuppressedByDnd:
        return "DND";
      case AlertReplayEventKind.Fired:
      default:
        return severityLabel(ev.severity);
    }
  }

  const markers = $derived.by<Marker[]>(() => {
    if (!xDomain) return [];
    const startMs = xDomain[0].getTime();
    const endMs = xDomain[1].getTime();
    return (result.events ?? [])
      .map((ev) => {
        const t = ev.at ? new Date(ev.at).getTime() : NaN;
        if (!Number.isFinite(t) || t < startMs || t > endMs) return null;
        return { ev, tMs: t };
      })
      .filter((m): m is Marker => m !== null);
  });

  // ---- Manual playback (rAF) ----
  // rAF instead of Tween so we can reason about pause/scrub deterministically.
  // BASE_ANIMATION_MS is the wall-clock time for a 1x sweep across the window;
  // the active duration is BASE / speed.
  const BASE_ANIMATION_MS = 12_000;
  let speed = $state<number>(1);
  const animationMs = $derived(BASE_ANIMATION_MS / speed);

  let playPct = $state(0);
  let maxPct = $state(0);
  let playing = $state(false);
  let rafId: number | null = null;
  let lastTs: number | null = null;

  function tick(ts: number): void {
    if (!playing) {
      rafId = null;
      return;
    }
    if (lastTs == null) lastTs = ts;
    const dt = ts - lastTs;
    lastTs = ts;
    const next = Math.min(100, playPct + (dt / animationMs) * 100);
    playPct = next;
    if (next > maxPct) maxPct = next;
    if (next >= 100) {
      playing = false;
      rafId = null;
      lastTs = null;
      return;
    }
    rafId = requestAnimationFrame(tick);
  }

  function play(): void {
    if (playing) return;
    if (playPct >= 100) {
      playPct = 0;
      maxPct = 0;
    }
    playing = true;
    lastTs = null;
    rafId = requestAnimationFrame(tick);
  }

  function pause(): void {
    playing = false;
    if (rafId != null) cancelAnimationFrame(rafId);
    rafId = null;
    lastTs = null;
  }

  function togglePlayback(): void {
    if (playing) pause();
    else play();
  }

  function resetPlayback(): void {
    pause();
    playPct = 0;
    maxPct = 0;
  }

  function seek(pct: number): void {
    pause();
    playPct = Math.max(0, Math.min(100, pct));
    if (playPct > maxPct) maxPct = playPct;
  }

  // A new window plays from its start once its chart has data, so the playhead
  // never sweeps ahead of the trace. A new result over the same window keeps
  // the playhead where it is.
  let readyWindow = $state<string | null>(null);
  $effect(() => {
    if (readyWindow === windowKey && windowKey) {
      untrack(() => {
        resetPlayback();
        play();
      });
    }
  });

  onDestroy(() => pause());

  const isEmpty = $derived(markers.length === 0);

  const currentTimeMs = $derived.by<number | null>(() => {
    if (!xDomain) return null;
    const [s, e] = xDomain;
    return s.getTime() + ((e.getTime() - s.getTime()) * playPct) / 100;
  });

  const currentDate = $derived(currentTimeMs != null ? new Date(currentTimeMs) : null);

  const firedMarkers = $derived(
    currentTimeMs != null ? markers.filter((m) => m.tMs <= currentTimeMs) : []
  );

  // Replay events land on the same 5-min ticks the chart's glucose readings
  // do, so a half-tick window catches all events for the hovered point
  // without bleeding into neighbouring ones.
  const TOOLTIP_HALF_WINDOW_MS = 2.5 * 60 * 1000;
  function eventsNear(at: Date): Marker[] {
    const t = at.getTime();
    return markers.filter((m) => Math.abs(m.tMs - t) <= TOOLTIP_HALF_WINDOW_MS);
  }
</script>

{#snippet replayTooltipExtras({ time: at }: { time: Date })}
  {@const nearby = eventsNear(at)}
  {#each nearby as m, i (`${m.ev.ruleId ?? "x"}:${m.tMs}:${m.ev.kind ?? ""}:${i}`)}
    <Tooltip.Item
      label={kindLabel(m.ev)}
      value={m.ev.ruleName ?? "(unnamed rule)"}
      color={severityVar(m.ev.severity)}
      class="font-medium"
    />
  {/each}
{/snippet}

{#if xDomain}
  <div
    class="grid gap-4 @2xl:min-h-0 @2xl:flex-1 @2xl:grid-cols-[minmax(0,1fr)_280px] @2xl:items-stretch @4xl:grid-cols-[minmax(0,1fr)_320px]"
  >
    <!-- Chart + playback + events list (left on wide containers, full width on narrow) -->
    <div class="flex min-w-0 min-h-0 flex-col gap-4">
      <div class="rounded-md border bg-background p-1">
        {#key windowKey}
          {@const key = windowKey}
          {@const engine = chartEngine({
            range: { from: xDomain[0], to: xDomain[1] },
            onDataReady: () => (readyWindow = key),
          })}
          <GlucoseChartShell
            {engine}
            {legend}
            heightClass="h-[280px]"
            {onSelectionChange}
          >
            {#snippet tracks()}
              <BasalTrack />
              <ThresholdRules />
              <GlucoseTrack />
              <IobCobTrack />
              <ReplayOverlay {firedMarkers} {currentDate} />
            {/snippet}
            {#snippet overlays()}
              <ChartTooltip tooltipExtras={replayTooltipExtras} {onTimeClick} />
            {/snippet}
          </GlucoseChartShell>
        {/key}
      </div>

      <PlaybackStrip
        {playing}
        {playPct}
        {maxPct}
        {currentDate}
        bind:speed
        events={markers.map((m) => ({
          tMs: m.tMs,
          severity: m.ev.severity,
          ruleId: m.ev.ruleId ?? undefined,
          kind: m.ev.kind,
        }))}
        windowStartMs={xDomain[0].getTime()}
        windowEndMs={xDomain[1].getTime()}
        onPlayPause={togglePlayback}
        onReset={resetPlayback}
        onSeek={seek}
      />

      {#if isEmpty}
        <div
          class="@2xl:flex-1 @2xl:min-h-0 rounded-md border bg-muted/30 px-4 py-6 text-center text-sm text-muted-foreground"
        >
          No events would have fired in this window.
        </div>
      {:else if firedMarkers.length === 0}
        <div
          class="@2xl:flex-1 @2xl:min-h-0 rounded-md border border-dashed py-6 text-center text-xs text-muted-foreground"
        >
          No events yet — playhead at start of window.
        </div>
      {:else}
        <div
          class="max-h-72 overflow-y-auto rounded-md border divide-y @2xl:max-h-none @2xl:flex-1 @2xl:min-h-0"
        >
          {#each firedMarkers as m, i (`${m.ev.ruleId ?? "x"}:${m.tMs}:${m.ev.kind ?? ""}:${i}`)}
            {@const dimmed = currentTimeMs != null && m.tMs > currentTimeMs}
            {@const isResolved = m.ev.kind === AlertReplayEventKind.AutoResolved}
            {@const isSuppressed = m.ev.kind === AlertReplayEventKind.SuppressedByDnd}
            <div
              class="flex items-center gap-3 px-3 py-2 text-sm transition-opacity duration-150"
              class:opacity-40={dimmed}
              class:text-muted-foreground={isSuppressed}
            >
              {#if isResolved}
                <CheckCircle2
                  class="h-3.5 w-3.5 shrink-0 text-(--severity)"
                  style="--severity: {severityVar(m.ev.severity)}"
                  aria-hidden="true"
                />
              {:else if isSuppressed}
                <BellOff class="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
              {:else}
                <Bell
                  class="h-3.5 w-3.5 shrink-0 text-(--severity)"
                  style="--severity: {severityVar(m.ev.severity)}"
                  aria-hidden="true"
                />
              {/if}
              <span class="font-mono text-xs text-muted-foreground tabular-nums w-16 shrink-0">
                {m.ev.at ? time(new Date(m.ev.at)) : ""}
              </span>
              <Badge variant="outline" class="shrink-0">
                {kindLabel(m.ev)}
              </Badge>
              <span class="flex-1 min-w-0 truncate">
                {m.ev.ruleName ?? "(unnamed rule)"}
              </span>
            </div>
          {/each}
        </div>
      {/if}
    </div>

    <!-- Rule sidebar (right on wide containers, stacked under on narrow) -->
    {#if currentTimeMs != null}
      <div class="@2xl:min-h-0 @2xl:overflow-y-auto">
        <RuleSidebar
          {rules}
          {editingRuleId}
          {treeByRule}
          {leafIdsByRule}
          {leafLog}
          {factLog}
          {currentTimeMs}
          expanded={fixedRules}
          toggleable={!fixedRules}
          bind:disabledRuleIds
          availableRules={rules
            .filter((r): r is AlertRuleResponse & { id: string } => !!r.id)
            .map((r) => ({ id: r.id, name: r.name ?? "" }))}
        />
      </div>
    {/if}
  </div>
{/if}
