<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Loader2,
    Info,
    AlertCircle,
    PlayCircle,
  } from "lucide-svelte";
  import {
    replay,
    replayDryRun,
  } from "$api/generated/alertReplays.generated.remote";
  import { getRules } from "$api/generated/alertRules.generated.remote";
  import type {
    AlertReplayResult,
    AlertReplayEvent,
    AlertRuleResponse,
    ReplayRuleDefinition,
  } from "$api-clients";
  import { severityLabel, severityVar } from "./severity";
  import { formatTime, formatDateTime, formatRange } from "./alertTime";
  import GlucoseChartCard from "$lib/components/dashboard/glucose-chart/GlucoseChartCard.svelte";
  import PlaybackStrip from "./PlaybackStrip.svelte";
  import RuleSidebar from "./RuleSidebar.svelte";
  import { LeafTransitionLog, assignLeafIds } from "./leafEval";
  import {
    nodeFromApi,
    ensureCompositeRoot,
    type ConditionNode,
  } from "./types";

  interface Props {
    /**
     * Sibling rules used to seed the rule sidebar before the panel runs its
     * own fresh fetch in {@link handleRun}. The fresh fetch picks up rules
     * created since the parent loaded.
     */
    availableRules?: AlertRuleResponse[];
    /**
     * When set, pre-selects the "custom" preset and seeds the date input — lets
     * callers (e.g. clicking a historic firing) jump straight to that day's
     * replay without first picking the preset.
     */
    initialCustomDate?: string | undefined;
    /**
     * When provided, replays use the dry-run endpoint with this in-memory rule
     * definition layered over saved rules — lets the editor test unsaved
     * changes before persisting them. The function form is re-evaluated on
     * each Run so edits made between presses are picked up.
     */
    rule?: ReplayRuleDefinition | (() => ReplayRuleDefinition);
    /** Pinned to the top of the sidebar with an "(editing)" marker. */
    editingRuleId?: string;
    /**
     * Live tree of the rule under edit. Used when building the per-rule tree
     * map so leaves the user is currently typing reflect back into the
     * sidebar's truth pips at the next replay tick.
     */
    editingTree?: ConditionNode;
  }

  let {
    availableRules = [],
    initialCustomDate,
    rule,
    editingRuleId,
    editingTree,
  }: Props = $props();

  type Preset = "last24h" | "7daysAgo" | "custom";
  // svelte-ignore state_referenced_locally
  let preset = $state<Preset>(initialCustomDate ? "custom" : "last24h");
  // svelte-ignore state_referenced_locally
  let customDate = $state<string>(initialCustomDate ?? "");

  const browserTimezone =
    typeof Intl !== "undefined"
      ? Intl.DateTimeFormat().resolvedOptions().timeZone
      : "UTC";

  let running = $state(false);
  let runError = $state<string | null>(null);
  let result = $state<AlertReplayResult | null>(null);

  // Per-run derived state populated by handleRun. Kept as plain $state (not
  // $derived) because they're built imperatively from a one-shot fetch.
  let allRules = $state<AlertRuleResponse[]>([]);
  let treeByRule = $state<Map<string, ConditionNode>>(new Map());
  let leafIdsByRule = $state<Map<string, Map<string, number>>>(new Map());
  let leafLog = $state<LeafTransitionLog>(new LeafTransitionLog({}));
  let disabledRuleIds = $state<Set<string>>(new Set());

  function resolvePreset(): { date: string | null } {
    switch (preset) {
      case "last24h":
        return { date: null };
      case "7daysAgo":
        return { date: ymd(daysAgo(7)) };
      case "custom":
        return { date: customDate || null };
    }
  }

  function daysAgo(n: number): Date {
    const d = new Date();
    d.setDate(d.getDate() - n);
    return d;
  }

  function ymd(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  async function handleRun(): Promise<void> {
    if (running) return;
    running = true;
    runError = null;
    result = null;
    pause();
    playPct = 0;
    maxPct = 0;
    try {
      const { date } = resolvePreset();
      const replayResult = rule
        ? await replayDryRun({
            date: date as unknown as Date | undefined,
            timezone: browserTimezone,
            rule: typeof rule === "function" ? rule() : rule,
          })
        : await replay({
            date: date as unknown as Date | undefined,
            timezone: browserTimezone,
          });
      result = replayResult ?? null;

      // Pull a fresh rule list so the sidebar sees rules created since the
      // parent loaded. Falls back to the seeded availableRules prop on error.
      let rulesList: AlertRuleResponse[] = availableRules;
      try {
        const fresh = await getRules();
        if (fresh && fresh.length > 0) rulesList = fresh;
      } catch {
        // Fall through to the seed list.
      }
      allRules = rulesList;

      // Build per-rule tree + leaf-id maps. The rule under edit substitutes
      // its in-memory tree so the sidebar reflects the editor's current
      // typing rather than the saved version.
      const trees = new Map<string, ConditionNode>();
      const ids = new Map<string, Map<string, number>>();
      for (const r of rulesList) {
        if (!r.id) continue;
        let parsed: ConditionNode | null;
        if (editingRuleId && r.id === editingRuleId && editingTree) {
          parsed = editingTree;
        } else {
          parsed = nodeFromApi(r.conditionType, r.conditionParams);
        }
        if (!parsed) continue;
        const tree = ensureCompositeRoot(parsed);
        trees.set(r.id, tree);
        ids.set(r.id, assignLeafIds(tree));
      }
      treeByRule = trees;
      leafIdsByRule = ids;
      leafLog = new LeafTransitionLog(result?.leafTransitionsByRule ?? {});
    } catch (err) {
      runError =
        err instanceof Error
          ? err.message
          : "Failed to run replay. Please try again.";
    } finally {
      running = false;
    }
  }

  let xDomain = $derived.by<[Date, Date] | undefined>(() => {
    if (!result?.windowStart || !result?.windowEnd) return undefined;
    return [new Date(result.windowStart), new Date(result.windowEnd)];
  });

  type Marker = { ev: AlertReplayEvent; tMs: number };
  let markers = $derived.by<Marker[]>(() => {
    if (!xDomain) return [];
    const startMs = xDomain[0].getTime();
    const endMs = xDomain[1].getTime();
    return (result?.events ?? [])
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
  let animationMs = $derived(BASE_ANIMATION_MS / speed);

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

  // Auto-start playback on each new result. untrack so the effect doesn't
  // re-fire on every animation frame (which would silently restart pausing).
  $effect(() => {
    if (result && xDomain) untrack(() => play());
  });

  onDestroy(() => pause());

  let hasRun = $derived(result !== null);
  let isEmpty = $derived(hasRun && (result?.events?.length ?? 0) === 0);

  let currentTimeMs = $derived.by<number | null>(() => {
    if (!xDomain) return null;
    const [s, e] = xDomain;
    return s.getTime() + ((e.getTime() - s.getTime()) * playPct) / 100;
  });

  let currentDate = $derived(
    currentTimeMs != null ? new Date(currentTimeMs) : null,
  );

  let firedMarkers = $derived(
    currentTimeMs != null
       ? markers.filter((m) => m.tMs <= currentTimeMs)
      : [],
  );

  // Auto-Run on mount so the panel demonstrates immediately.
  $effect(() => {
    if (!hasRun && !running) handleRun();
  });

  // Type alias mirrors the GlucoseChartCard `annotations` snippet payload.
  type AnnotationProps = {
    xScale: import("d3-scale").ScaleTime<number, number>;
    yScale: import("d3-scale").ScaleLinear<number, number>;
    width: number;
    height: number;
    padding: { top: number; right: number; bottom: number; left: number };
  };
</script>

{#snippet replayAnnotations({ xScale, height }: AnnotationProps)}
  {#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
    {@const px = xScale(new Date(m.tMs))}
    <line
      x1={px}
      x2={px}
      y1={height - 20}
      y2={height - 8}
      stroke={severityVar(m.ev.severity)}
      stroke-width="1.5"
    />
    <circle
      cx={px}
      cy={height - 8}
      r="4"
      fill={severityVar(m.ev.severity)}
    />
  {/each}
  {#if currentDate}
    {@const px = xScale(currentDate)}
    <line
      x1={px}
      x2={px}
      y1="0"
      y2={height}
      class="stroke-foreground/80"
      stroke-width="1.5"
    />
  {/if}
{/snippet}

<div class="space-y-4">
  <div class="flex flex-wrap items-center gap-2">
    {@render presetChip("last24h", "Last 24h")}
    {@render presetChip("7daysAgo", "7 days ago")}
    {@render presetChip("custom", "Pick a date…")}
    {#if preset === "custom"}
      <Input
        type="date"
        class="h-8 w-40 text-sm"
        bind:value={customDate}
        aria-label="Custom date"
      />
    {/if}
    <span class="flex-1"></span>
    <Button
      onclick={handleRun}
      disabled={running || (preset === "custom" && !customDate)}
    >
      {#if running}
        <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        Running…
      {:else}
        <PlayCircle class="h-4 w-4 mr-2" />
        Run
      {/if}
    </Button>
  </div>

  {#if runError}
    <div
      class="flex items-start gap-2 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
      role="alert"
    >
      <AlertCircle class="h-4 w-4 mt-0.5 flex-none" />
      <p>{runError}</p>
    </div>
  {/if}

  {#if hasRun && result}
    {#if result.windowStart && result.windowEnd}
      <p class="text-xs text-muted-foreground">
        Window: {formatRange(result.windowStart, result.windowEnd)}
      </p>
    {/if}

    {#if xDomain}
      <div class="rounded-md border bg-background p-1">
        <GlucoseChartCard
          compact
          dateRange={{ from: xDomain[0], to: xDomain[1] }}
          annotations={replayAnnotations}
          heightClass="h-[280px]"
        />
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
        }))}
        windowStartMs={xDomain[0].getTime()}
        windowEndMs={xDomain[1].getTime()}
        onPlayPause={togglePlayback}
        onReset={resetPlayback}
        onSeek={seek}
      />

      <div class="grid gap-4 md:grid-cols-[1fr_320px]">
        <!-- Events list (left) -->
        <div class="min-w-0">
          {#if isEmpty}
            <div
              class="rounded-md border bg-muted/30 px-4 py-6 text-center text-sm text-muted-foreground"
            >
              No events would have fired in this window.
            </div>
          {:else if firedMarkers.length === 0}
            <div
              class="rounded-md border border-dashed py-6 text-center text-xs text-muted-foreground"
            >
              No events yet — playhead at start of window.
            </div>
          {:else}
            <div class="max-h-72 overflow-y-auto rounded-md border divide-y">
              {#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
                {@const dimmed =
                  currentTimeMs != null && m.tMs > currentTimeMs}
                <div
                  class="flex items-center gap-3 px-3 py-2 text-sm transition-opacity duration-150"
                  class:opacity-40={dimmed}
                >
                  <span
                    class="h-2 w-2 shrink-0 rounded-full"
                    style:background-color={severityVar(m.ev.severity)}
                    aria-hidden="true"
                  ></span>
                  <span
                    class="font-mono text-xs text-muted-foreground tabular-nums w-16 shrink-0"
                  >
                    {formatTime(m.ev.at)}
                  </span>
                  <Badge variant="outline" class="shrink-0">
                    {severityLabel(m.ev.severity)}
                  </Badge>
                  <span class="flex-1 min-w-0 truncate">
                    {m.ev.ruleName ?? "(unnamed rule)"}
                  </span>
                </div>
              {/each}
            </div>
          {/if}
        </div>

        <!-- Rule sidebar (right) -->
        {#if currentTimeMs != null}
          <RuleSidebar
            rules={allRules}
            {editingRuleId}
            {treeByRule}
            {leafIdsByRule}
            {leafLog}
            currentTimeMs={currentTimeMs}
            bind:disabledRuleIds
            availableRules={allRules
              .filter((r) => r.id)
              .map((r) => ({ id: r.id as string, name: r.name ?? "" }))}
          />
        {/if}
      </div>
    {/if}

    {#if result.limitations}
      <div
        class="flex items-start gap-2 rounded-md border bg-muted/30 p-3 text-xs text-muted-foreground"
      >
        <Info class="h-4 w-4 mt-0.5 flex-none" />
        <p class="whitespace-pre-wrap">{result.limitations}</p>
      </div>
    {/if}
  {/if}
</div>

{#snippet presetChip(value: Preset, label: string)}
  <button
    type="button"
    class="rounded-md border px-2.5 py-1 text-xs font-medium {preset === value
      ? 'bg-primary text-primary-foreground border-primary'
      : 'bg-background text-muted-foreground hover:bg-muted'}"
    onclick={() => (preset = value)}
    aria-pressed={preset === value}
  >
    {label}
  </button>
{/snippet}
