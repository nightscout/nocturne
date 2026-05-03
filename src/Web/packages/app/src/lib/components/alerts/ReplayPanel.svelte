<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import { goto } from "$app/navigation";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Loader2,
    Info,
    AlertCircle,
    PlayCircle,
    Play,
    Pause,
    RotateCcw,
    Pencil,
  } from "lucide-svelte";
  import {
    Chart,
    Svg,
    Spline,
    Area,
    Threshold,
    AnnotationRange,
    AnnotationPoint,
    Tooltip,
  } from "layerchart";
  import { curveMonotoneX } from "d3-shape";
  import type { ScaleTime } from "d3-scale";
  import {
    replay,
    replayDryRun,
  } from "$api/generated/alertReplays.generated.remote";
  import { getDashboardChartData } from "$api/generated/chartDatas.generated.remote";
  import type {
    AlertReplayResult,
    AlertReplayEvent,
    AlertRuleResponse,
    GlucosePointDto,
    ReplayRuleDefinition,
  } from "$api-clients";
  import { severityLabel, severityVar } from "./severity";
  import { formatTime, formatDateTime, formatRange } from "./alertTime";

  interface Props {
    /**
     * Sibling rules — currently unused by the panel but kept on the prop shape
     * so the overview can pass the same list it shows in the table (future:
     * per-rule overlay toggle).
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
  }

  let { availableRules = [], initialCustomDate, rule }: Props = $props();
  void availableRules;

  type Preset = "last24h" | "7daysAgo" | "custom";
  let preset = $state<Preset>(initialCustomDate ? "custom" : "last24h");
  let customDate = $state<string>(initialCustomDate ?? "");

  const browserTimezone =
    typeof Intl !== "undefined"
      ? Intl.DateTimeFormat().resolvedOptions().timeZone
      : "UTC";

  let running = $state(false);
  let runError = $state<string | null>(null);
  let result = $state<AlertReplayResult | null>(null);
  let glucose = $state<GlucosePointDto[]>([]);

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
    glucose = [];
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

      const start = result?.windowStart
        ? new Date(result.windowStart).getTime()
        : null;
      const end = result?.windowEnd
        ? new Date(result.windowEnd).getTime()
        : null;
      if (start && end) {
        try {
          const chart = await getDashboardChartData({
            startTime: start,
            endTime: end,
          });
          glucose = chart?.glucoseData ?? [];
        } catch {
          glucose = [];
        }
      }
    } catch (err) {
      runError =
        err instanceof Error
          ? err.message
          : "Failed to run replay. Please try again.";
    } finally {
      running = false;
    }
  }

  let chartData = $derived(
    glucose
      .filter((g) => g.time != null && g.sgv != null)
      .map((g) => ({
        date: new Date(g.time as number),
        value: g.sgv as number,
      }))
      .sort((a, b) => a.date.getTime() - b.date.getTime())
  );

  let xDomain = $derived.by<[Date, Date] | undefined>(() => {
    if (!result?.windowStart || !result?.windowEnd) return undefined;
    return [new Date(result.windowStart), new Date(result.windowEnd)];
  });

  const Y_FLOOR = 40;
  const Y_CEIL = 350;
  const LOW = 70;
  const HIGH = 180;
  let yDomain = $derived.by<[number, number]>(() => {
    if (chartData.length === 0) return [Y_FLOOR, Y_CEIL];
    const max = Math.max(Y_CEIL - 50, ...chartData.map((p) => p.value));
    const min = Math.min(Y_FLOOR + 10, ...chartData.map((p) => p.value));
    return [Math.max(Y_FLOOR, min - 10), Math.min(Y_CEIL, max + 10)];
  });

  type Marker = { ev: AlertReplayEvent; tMs: number; xPct: number };
  let markers = $derived.by<Marker[]>(() => {
    if (!xDomain) return [];
    const [start, end] = xDomain;
    const startMs = start.getTime();
    const endMs = end.getTime();
    const span = endMs - startMs || 1;
    return (result?.events ?? [])
      .map((ev) => {
        const t = ev.at ? new Date(ev.at).getTime() : NaN;
        if (!Number.isFinite(t) || t < startMs || t > endMs) return null;
        return { ev, tMs: t, xPct: ((t - startMs) / span) * 100 };
      })
      .filter((m): m is Marker => m !== null);
  });

  // ---- Manual playback (rAF) ----
  // We drive the playhead with requestAnimationFrame instead of svelte's
  // Tween so we can reason about state precisely. The previous Tween-based
  // implementation was being interrupted by reactive churn in the
  // AnnotationRange mask, leaving each "play" press only advancing a tick.
  const ANIMATION_MS = 12000;
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
    const next = Math.min(100, playPct + (dt / ANIMATION_MS) * 100);
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

  // Auto-start the sweep each time a new result arrives. `play()` reads
  // `playing` and `playPct` reactively, so without `untrack` this effect
  // would re-fire on every animation tick and on every pause — silently
  // restarting playback and making the pause button look broken.
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
    currentTimeMs != null ? new Date(currentTimeMs) : null
  );

  // High-water mark in absolute time. Used to decide how much of the
  // chart to reveal — the mask shrinks from the right toward this point
  // and never grows back, so the line stays visible if the user scrubs.
  let maxSeenDate = $derived.by<Date | null>(() => {
    if (!xDomain) return null;
    const [s, e] = xDomain;
    return new Date(s.getTime() + ((e.getTime() - s.getTime()) * maxPct) / 100);
  });

  // Linear interpolation along chartData so the playhead crosshair sits on
  // the line at the current time.
  function valueAt(ms: number): number | null {
    if (chartData.length === 0) return null;
    if (ms <= chartData[0].date.getTime()) return chartData[0].value;
    const last = chartData[chartData.length - 1];
    if (ms >= last.date.getTime()) return last.value;
    for (let i = 1; i < chartData.length; i++) {
      const a = chartData[i - 1];
      const b = chartData[i];
      const bMs = b.date.getTime();
      if (ms <= bMs) {
        const aMs = a.date.getTime();
        const t = (ms - aMs) / (bMs - aMs);
        return a.value + t * (b.value - a.value);
      }
    }
    return null;
  }

  let firedMarkers = $derived(markers.filter((m) => m.xPct <= maxPct));

  // Auto-Run on mount so the panel demonstrates immediately.
  $effect(() => {
    if (!hasRun && !running) handleRun();
  });

  function editRule(ruleId: string | undefined): void {
    if (!ruleId) return;
    goto(`/alerts/${ruleId}`);
  }

  // ---- Pointer scrubbing on the chart ----

  type TimeScale = ScaleTime<number, number>;
  type ChartContext = {
    width: number;
    height: number;
    xScale: TimeScale;
    tooltip?: {
      show: (e: PointerEvent | MouseEvent, data: unknown) => void;
      hide: () => void;
    };
  };

  type ReplayTooltip = {
    time: Date;
    value: number | null;
  };

  let scrubbing = $state(false);

  function pointerToPct(
    e: PointerEvent,
    xScale: TimeScale,
    width: number
  ): number | null {
    const svg = (e.currentTarget as SVGElement).closest("svg");
    if (!svg || !xDomain) return null;
    const r = svg.getBoundingClientRect();
    // The chart has padding on left/right; xScale.invert handles that since
    // it's keyed off the rendered range, not the SVG width. We clamp on the
    // domain after inverting.
    const localX = Math.max(0, Math.min(width, e.clientX - r.left));
    const t = xScale.invert(localX).getTime();
    const startMs = xDomain[0].getTime();
    const endMs = xDomain[1].getTime();
    const span = endMs - startMs || 1;
    const pct = ((t - startMs) / span) * 100;
    return Math.max(0, Math.min(100, pct));
  }

  function setPlayheadFromPointer(
    e: PointerEvent,
    xScale: TimeScale,
    width: number
  ): void {
    const pct = pointerToPct(e, xScale, width);
    if (pct == null) return;
    playPct = pct;
    if (pct > maxPct) maxPct = pct;
  }

  function showTooltipAt(e: PointerEvent, context: ChartContext): void {
    if (!xDomain) return;
    const localTime = context.xScale.invert(
      Math.max(
        0,
        Math.min(
          context.width,
          e.clientX -
            ((e.currentTarget as SVGElement)
              .closest("svg")
              ?.getBoundingClientRect().left ?? 0)
        )
      )
    );
    const v = valueAt(localTime.getTime());
    context.tooltip?.show(e, {
      time: localTime,
      value: v,
    } satisfies ReplayTooltip);
  }
</script>

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

    {#if xDomain && chartData.length > 0}
      <div
        class="h-48 w-full rounded-md border bg-background touch-none"
        class:cursor-grabbing={scrubbing}
        class:cursor-crosshair={!scrubbing}
      >
        <Chart
          data={chartData}
          x="date"
          y="value"
          {xDomain}
          {yDomain}
          padding={{ top: 8, bottom: 8, left: 8, right: 8 }}
          tooltip={{ mode: "manual" }}
        >
          {#snippet children({ context })}
            {@const ctx = {
              width: context.width,
              height: context.height,
              xScale: context.xScale as unknown as TimeScale,
              tooltip: context.tooltip,
            } satisfies ChartContext}
            <Svg>
              <AnnotationRange
                y={[LOW, HIGH]}
                class="opacity-15 fill-[var(--glucose-in-range)]"
              />
              <Threshold curve={curveMonotoneX}>
                {#snippet above()}
                  <Area
                    y0={HIGH}
                    curve={curveMonotoneX}
                    class="fill-[var(--glucose-high)]/30"
                    line={{ class: "stroke-none" }}
                  />
                {/snippet}
                {#snippet below()}
                  <Area
                    y0={LOW}
                    curve={curveMonotoneX}
                    class="fill-[var(--glucose-low)]/30"
                    line={{ class: "stroke-none" }}
                  />
                {/snippet}
                <Spline
                  curve={curveMonotoneX}
                  class="stroke-foreground/70 stroke-[1.5] fill-none"
                />
              </Threshold>

              <!-- Reveal mask: covers the still-undiscovered portion of the
                   window. Keyed off the high-water mark, not the playhead,
                   so scrubbing back never re-hides the line. -->
              {#if maxSeenDate && xDomain[1].getTime() > maxSeenDate.getTime()}
                {@const maskX = ctx.xScale(maxSeenDate)}
                {@const maskRight = ctx.xScale(xDomain[1])}
                <rect
                  x={maskX}
                  y={0}
                  width={Math.max(0, maskRight - maskX)}
                  height={ctx.height}
                  fill="var(--background)"
                />
              {/if}

              <!-- Discovered alerts. AnnotationPoint colored by severity;
                   dims when the playhead is scrubbed back behind them. -->
              {#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
                {@const v = valueAt(m.tMs)}
                {#if v != null}
                  {@const dimmed =
                    currentTimeMs != null && m.tMs > currentTimeMs}
                  {@const c = severityVar(m.ev.severity)}
                  <!-- AnnotationPoint only forwards styling via props.circle;
                       fill/stroke at the top level are dropped on the floor. -->
                  <AnnotationPoint
                    x={new Date(m.tMs)}
                    y={v}
                    r={5}
                    props={{
                      circle: {
                        fill: c,
                        stroke: c,
                        strokeWidth: 1.5,
                        fillOpacity: dimmed ? 0.4 : 1,
                        strokeOpacity: dimmed ? 0.6 : 1,
                      },
                    }}
                  />
                {/if}
              {/each}

              <!-- Playhead vertical rule -->
              {#if currentDate}
                {@const px = ctx.xScale(currentDate)}
                <line
                  x1={px}
                  x2={px}
                  y1={0}
                  y2={ctx.height}
                  class="stroke-foreground/80"
                  stroke-width="1.5"
                />
                {@const pv =
                  currentTimeMs != null ? valueAt(currentTimeMs) : null}
                {#if pv != null && context.yScale}
                  <circle
                    cx={px}
                    cy={(context.yScale as (v: number) => number)(pv)}
                    r={4}
                    class="fill-foreground stroke-background"
                    stroke-width="1.5"
                  />
                {/if}
              {/if}

              <!-- Pointer surface. Click + drag to scrub; hover for tooltip. -->
              <rect
                role="presentation"
                x={0}
                y={0}
                width={ctx.width}
                height={ctx.height}
                fill="transparent"
                onpointerdown={(e) => {
                  pause();
                  scrubbing = true;
                  (e.currentTarget as SVGRectElement).setPointerCapture(
                    e.pointerId
                  );
                  setPlayheadFromPointer(e, ctx.xScale, ctx.width);
                  showTooltipAt(e, ctx);
                }}
                onpointermove={(e) => {
                  if (scrubbing) {
                    setPlayheadFromPointer(e, ctx.xScale, ctx.width);
                  }
                  showTooltipAt(e, ctx);
                }}
                onpointerup={(e) => {
                  scrubbing = false;
                  (e.currentTarget as SVGRectElement).releasePointerCapture(
                    e.pointerId
                  );
                }}
                onpointercancel={() => {
                  scrubbing = false;
                }}
                onpointerleave={() => {
                  ctx.tooltip?.hide();
                }}
              />
            </Svg>

            <Tooltip.Root
              class="bg-popover/95 text-popover-foreground rounded-md border border-border px-2 py-1 shadow-md text-xs"
            >
              {#snippet children({ data })}
                {@const d = data as ReplayTooltip | undefined}
                {#if d}
                  <div class="space-y-0.5">
                    <div class="font-medium tabular-nums">
                      {formatDateTime(d.time)}
                    </div>
                    {#if d.value != null}
                      <div class="flex items-center gap-1.5">
                        <span class="text-muted-foreground">Glucose</span>
                        <span class="font-mono font-medium tabular-nums">
                          {Math.round(d.value)}
                        </span>
                      </div>
                    {/if}
                  </div>
                {/if}
              {/snippet}
            </Tooltip.Root>
          {/snippet}
        </Chart>
      </div>
    {:else if isEmpty && xDomain}
      <div
        class="flex h-32 w-full items-center justify-center rounded-md border border-dashed text-sm text-muted-foreground"
      >
        No glucose data in this window.
      </div>
    {/if}

    {#if xDomain}
      <div class="flex items-center gap-2">
        <Button
          variant="outline"
          size="icon"
          class="h-8 w-8"
          onclick={togglePlayback}
          aria-label={playing ? "Pause" : "Play"}
        >
          {#if playing}
            <Pause class="h-4 w-4" />
          {:else}
            <Play class="h-4 w-4" />
          {/if}
        </Button>
        <Button
          variant="outline"
          size="icon"
          class="h-8 w-8"
          onclick={resetPlayback}
          aria-label="Reset"
        >
          <RotateCcw class="h-4 w-4" />
        </Button>
        <span
          class="font-mono text-xs text-muted-foreground tabular-nums ml-auto shrink-0"
        >
          {currentDate ? formatDateTime(currentDate) : ""}
        </span>
      </div>
    {/if}

    {#if isEmpty}
      <div
        class="rounded-md border bg-muted/30 px-4 py-6 text-center text-sm text-muted-foreground"
      >
        No events would have fired in this window.
      </div>
    {:else if firedMarkers.length > 0}
      <div class="max-h-72 overflow-y-auto rounded-md border divide-y">
        {#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
          {@const dimmed = currentTimeMs != null && m.tMs > currentTimeMs}
          <div
            class="group flex items-center gap-3 px-3 py-2 text-sm transition-opacity duration-150"
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
            {#if m.ev.ruleId}
              <Button
                variant="ghost"
                size="sm"
                class="h-7 px-2 opacity-0 group-hover:opacity-100 focus:opacity-100"
                onclick={() => editRule(m.ev.ruleId)}
                aria-label="Edit rule"
              >
                <Pencil class="h-3.5 w-3.5 mr-1" />
                Edit
              </Button>
            {/if}
          </div>
        {/each}
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
