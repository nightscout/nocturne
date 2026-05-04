<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import {
    type DateValue,
    getLocalTimeZone,
    parseDate,
    today,
  } from "@internationalized/date";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import GlucoseCalendarPicker from "./GlucoseCalendarPicker.svelte";
  import * as Popover from "$lib/components/ui/popover";
  import {
    Loader2,
    Info,
    AlertCircle,
    Calendar as CalendarIcon,
    CheckCircle2,
    BellOff,
    Bell,
  } from "lucide-svelte";
  import {
    replay,
    replayDryRun,
  } from "$api/generated/alertReplays.generated.remote";
  import { getRules } from "$api/generated/alertRules.generated.remote";
  import {
    AlertReplayEventKind,
    type AlertReplayResult,
    type AlertReplayEvent,
    type AlertRuleResponse,
    type ReplayRuleDefinition,
  } from "$api-clients";
  import { severityLabel, severityVar } from "./severity";
  import { formatTime, formatRange } from "./alertTime";
  import GlucoseChartCard from "$lib/components/dashboard/glucose-chart/GlucoseChartCard.svelte";
  import { Tooltip } from "layerchart";
  import PlaybackStrip from "./PlaybackStrip.svelte";
  import RuleSidebar from "./RuleSidebar.svelte";
  import { LeafTransitionLog, assignLeafIds } from "./leafEval";
  import { FactSnapshotLog } from "./factSnapshot";
  import {
    nodeFromApi,
    ensureCompositeRoot,
    type ConditionNode,
  } from "./types";

  interface Props {
    /**
     * Sibling rules used to seed the rule sidebar before the panel runs its own
     * fresh fetch in {@link handleRun}. The fresh fetch picks up rules created
     * since the parent loaded.
     */
    availableRules?: AlertRuleResponse[];
    /**
     * When set, pre-fills the date picker with this YYYY-MM-DD date — lets
     * callers (e.g. clicking a historic firing) jump straight to that day's
     * replay. When omitted, the panel defaults to a rolling last-24-hours
     * window.
     */
    initialCustomDate?: string | undefined;
    /**
     * When provided, replays use the dry-run endpoint with this in-memory rule
     * definition layered over saved rules — lets the editor test unsaved
     * changes before persisting them. The function form is re-evaluated on each
     * Run so edits made between presses are picked up.
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

  // Window state. `undefined` selectedDate + empty from/to → rolling last 24 hours.
  // A selectedDate alone replays that calendar day in the browser's timezone.
  // Non-empty fromInput/toInput take precedence and replay an arbitrary UTC range
  // (the values are local-time strings from <input type="datetime-local">; we
  // convert to UTC instants when dispatching).
  function parseInitialDate(s: string | undefined): DateValue | undefined {
    if (!s) return undefined;
    try {
      return parseDate(s);
    } catch {
      return undefined;
    }
  }
  // svelte-ignore state_referenced_locally
  let selectedDate = $state<DateValue | undefined>(
    parseInitialDate(initialCustomDate)
  );
  let datePickerOpen = $state(false);

  // <input type="time"> values: "HH:mm". Empty string = unset. The day comes
  // from `selectedDate` (or "today" in the local zone when no date is picked);
  // when toTime <= fromTime we wrap to the next day, so "15:42 → 05:23"
  // produces an overnight window without needing a second date input.
  let fromTime = $state<string>("");
  let toTime = $state<string>("");

  // Brush selection on the chart drives the replay window directly. While
  // active it overrides `fromTime`/`toTime`, and we mirror the times back into
  // the inputs so the user can see (and tweak) the selection numerically.
  let brushDomain = $state<[Date, Date] | null>(null);

  const browserTimezone =
    typeof Intl !== "undefined"
      ? Intl.DateTimeFormat().resolvedOptions().timeZone
      : "UTC";

  let running = $state(false);
  let runError = $state<string | null>(null);
  let result = $state<AlertReplayResult | null>(null);

  // Parse a "HH:mm" string into [hours, minutes]. Returns null on bad input.
  function parseHHmm(s: string): [number, number] | null {
    const m = /^(\d{1,2}):(\d{2})$/.exec(s);
    if (!m) return null;
    const h = Number(m[1]);
    const min = Number(m[2]);
    if (h < 0 || h > 23 || min < 0 || min > 59) return null;
    return [h, min];
  }

  // Compose the absolute From/To instants from selectedDate (or today) plus
  // the HH:mm time inputs. When toTime is at-or-before fromTime, the To side
  // wraps to the next day so e.g. "15:42 → 05:23" reads as overnight rather
  // than as a negative window.
  function computeRange(): { from: Date; to: Date } | null {
    if (brushDomain) {
      return { from: brushDomain[0], to: brushDomain[1] };
    }
    const fromHm = parseHHmm(fromTime);
    const toHm = parseHHmm(toTime);
    if (!fromHm || !toHm) return null;

    const baseLocal = selectedDate
      ? selectedDate.toDate(getLocalTimeZone())
      : (() => {
          const t = new Date();
          t.setHours(0, 0, 0, 0);
          return t;
        })();

    const from = new Date(baseLocal.getTime());
    from.setHours(fromHm[0], fromHm[1], 0, 0);
    const to = new Date(baseLocal.getTime());
    to.setHours(toHm[0], toHm[1], 0, 0);
    if (to.getTime() <= from.getTime()) {
      to.setDate(to.getDate() + 1);
    }
    return { from, to };
  }

  // Per-run derived state populated by handleRun. Kept as plain $state (not
  // $derived) because they're built imperatively from a one-shot fetch.
  let allRules = $state<AlertRuleResponse[]>([]);
  let treeByRule = $state<Map<string, ConditionNode>>(new Map());
  let leafIdsByRule = $state<Map<string, Map<string, number>>>(new Map());
  let leafLog = $state<LeafTransitionLog>(new LeafTransitionLog({}));
  let factLog = $state<FactSnapshotLog>(new FactSnapshotLog({}));
  let disabledRuleIds = $state<Set<string>>(new Set());

  function dateLabel(d: DateValue | undefined): string {
    if (!d) return "Last 24 hours";
    return d.toDate(getLocalTimeZone()).toLocaleDateString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  }

  function clearDate(): void {
    selectedDate = undefined;
    fromTime = "";
    toTime = "";
    brushDomain = null;
    datePickerOpen = false;
  }

  function handleDatePicked(value: DateValue | undefined): void {
    selectedDate = value;
    brushDomain = null;
    if (value) datePickerOpen = false;
  }

  // Format a Date as the "HH:mm" string the time inputs expect. Local-time
  // components so the picker shows wall clock, matching what the user sees
  // on the chart.
  function toHHmm(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  // Brush callback. The chart hands us absolute Date endpoints; we store
  // them verbatim (so the replay covers the brushed instants exactly even
  // when they straddle midnight) and mirror the times into the inputs so
  // the user can see — and fine-tune — the selection numerically.
  function handleBrushSelection(domain: [Date, Date] | null): void {
    if (!domain) {
      brushDomain = null;
      return;
    }
    const [from, to] = domain[0] <= domain[1] ? domain : [domain[1], domain[0]];
    brushDomain = [from, to];
    fromTime = toHHmm(from);
    toTime = toHHmm(to);
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
      const range = computeRange();
      const date = !range && selectedDate ? selectedDate.toString() : null;
      // Zod schema for from/to is `string (date-time)`. The generated client
      // types claim Date, but the request travels as JSON, so we send ISO
      // strings and cast for the type checker.
      const fromIso = range
        ? (range.from.toISOString() as unknown as Date)
        : undefined;
      const toIso = range
        ? (range.to.toISOString() as unknown as Date)
        : undefined;
      const replayResult = rule
        ? await replayDryRun({
            date: date as unknown as Date | undefined,
            timezone: browserTimezone,
            from: fromIso,
            to: toIso,
            rule: typeof rule === "function" ? rule() : rule,
          })
        : await replay({
            date: date as unknown as Date | undefined,
            timezone: browserTimezone,
            from: fromIso,
            to: toIso,
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
      factLog = new FactSnapshotLog(result?.factTimelines ?? {});
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

  // Kind-aware label for the events list and a11y. Falls back to the severity label
  // for legacy events that pre-date the discriminator.
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
    currentTimeMs != null ? new Date(currentTimeMs) : null
  );

  let firedMarkers = $derived(
    currentTimeMs != null ? markers.filter((m) => m.tMs <= currentTimeMs) : []
  );

  // Auto-run on mount and on every window-selection change. We track the
  // serialised window inputs so a re-pick of the same value doesn't re-fire,
  // but any actual change (date, from, to) triggers a fresh replay without
  // the user clicking anything. Runs that error out clear `running` in the
  // finally block, so the next change still fires.
  let lastRunKey = $state<string | null>(null);
  $effect(() => {
    const brushKey = brushDomain
      ? `${brushDomain[0].getTime()}-${brushDomain[1].getTime()}`
      : "";
    const key = `${selectedDate?.toString() ?? ""}|${fromTime}|${toTime}|${brushKey}`;
    if (running) return;
    if (key === lastRunKey) return;
    // Partial range — wait until the user has filled both endpoints rather
    // than firing a half-baked replay every keystroke.
    if (!brushDomain && ((fromTime && !toTime) || (!fromTime && toTime))) {
      return;
    }
    lastRunKey = key;
    untrack(() => handleRun());
  });

  // Type alias mirrors the GlucoseChartCard `annotations` snippet payload.
  type AnnotationProps = {
    xScale: import("d3-scale").ScaleTime<number, number>;
    yScale: import("d3-scale").ScaleLinear<number, number>;
    width: number;
    height: number;
    padding: { top: number; right: number; bottom: number; left: number };
  };

  // Replay events land on the same 5-min ticks the chart's glucose readings
  // do, so a half-tick window catches all events for the hovered point
  // without bleeding into neighbouring ones.
  const TOOLTIP_HALF_WINDOW_MS = 2.5 * 60 * 1000;
  function eventsNear(time: Date): Marker[] {
    const t = time.getTime();
    return markers.filter((m) => Math.abs(m.tMs - t) <= TOOLTIP_HALF_WINDOW_MS);
  }
</script>

{#snippet replayAnnotations({ xScale, height }: AnnotationProps)}
  {#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
    {@const px = xScale(new Date(m.tMs))}
    {@const color = severityVar(m.ev.severity)}
    {@const isResolved = m.ev.kind === AlertReplayEventKind.AutoResolved}
    {@const isSuppressed = m.ev.kind === AlertReplayEventKind.SuppressedByDnd}
    <line
      x1={px}
      x2={px}
      y1={height - 20}
      y2={height - 8}
      stroke={color}
      stroke-width="1.5"
      stroke-dasharray={isSuppressed ? "2 2" : null}
      opacity={isSuppressed ? 0.6 : 1}
    />
    <circle
      cx={px}
      cy={height - 8}
      r="4"
      fill={isResolved || isSuppressed ? "none" : color}
      stroke={color}
      stroke-width={isResolved || isSuppressed ? 1.5 : 0}
      opacity={isSuppressed ? 0.6 : 1}
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

{#snippet replayTooltipExtras({ time }: { time: Date })}
  {@const nearby = eventsNear(time)}
  {#each nearby as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
    <Tooltip.Item
      label={kindLabel(m.ev)}
      value={m.ev.ruleName ?? "(unnamed rule)"}
      color={severityVar(m.ev.severity)}
      class="font-medium"
    />
  {/each}
{/snippet}

<div class="@container flex h-full min-h-0 flex-col gap-4">
  <div class="flex flex-wrap items-center gap-2">
    <Popover.Root bind:open={datePickerOpen}>
      <Popover.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            variant="outline"
            class="h-8 justify-start gap-2 font-normal"
          >
            <CalendarIcon class="h-3.5 w-3.5 text-muted-foreground" />
            {dateLabel(selectedDate)}
          </Button>
        {/snippet}
      </Popover.Trigger>
      <Popover.Content class="w-auto overflow-hidden p-0" align="start">
        <div class="border-b p-2">
          <Button
            variant="ghost"
            size="sm"
            class="w-full justify-start text-xs"
            onclick={clearDate}
          >
            Last 24 hours
          </Button>
        </div>
        <GlucoseCalendarPicker
          value={selectedDate}
          onValueChange={handleDatePicked}
          maxValue={today(getLocalTimeZone())}
        />
      </Popover.Content>
    </Popover.Root>

    <label class="flex items-center gap-1.5 text-xs text-muted-foreground">
      From
      <input
        type="time"
        class="h-8 rounded-md border bg-background px-2 text-xs text-foreground"
        bind:value={fromTime}
        oninput={() => (brushDomain = null)}
      />
    </label>
    <label class="flex items-center gap-1.5 text-xs text-muted-foreground">
      To
      <input
        type="time"
        class="h-8 rounded-md border bg-background px-2 text-xs text-foreground"
        bind:value={toTime}
        oninput={() => (brushDomain = null)}
      />
    </label>

    {#if running}
      <span
        class="inline-flex items-center gap-1.5 text-xs text-muted-foreground"
      >
        <Loader2 class="h-3.5 w-3.5 animate-spin" />
        Running…
      </span>
    {/if}
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
      <div
        class="grid flex-1 min-h-0 gap-4 @3xl:grid-cols-[minmax(0,1fr)_320px] @3xl:items-stretch"
      >
        <!-- Chart + playback + events list (left on wide containers, full width on narrow) -->
        <div class="flex min-w-0 min-h-0 flex-col gap-4">
          <div class="rounded-md border bg-background p-1">
            <GlucoseChartCard
              compact
              dateRange={{ from: xDomain[0], to: xDomain[1] }}
              annotations={replayAnnotations}
              tooltipExtras={replayTooltipExtras}
              onSelectionChange={handleBrushSelection}
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
              class="flex-1 min-h-0 rounded-md border bg-muted/30 px-4 py-6 text-center text-sm text-muted-foreground"
            >
              No events would have fired in this window.
            </div>
          {:else if firedMarkers.length === 0}
            <div
              class="flex-1 min-h-0 rounded-md border border-dashed py-6 text-center text-xs text-muted-foreground"
            >
              No events yet — playhead at start of window.
            </div>
          {:else}
            <div
              class="flex-1 min-h-0 overflow-y-auto rounded-md border divide-y"
            >
              {#each firedMarkers as m (`${m.ev.ruleId ?? "x"}:${m.tMs}`)}
                {@const dimmed = currentTimeMs != null && m.tMs > currentTimeMs}
                {@const isResolved =
                  m.ev.kind === AlertReplayEventKind.AutoResolved}
                {@const isSuppressed =
                  m.ev.kind === AlertReplayEventKind.SuppressedByDnd}
                <div
                  class="flex items-center gap-3 px-3 py-2 text-sm transition-opacity duration-150"
                  class:opacity-40={dimmed}
                  class:text-muted-foreground={isSuppressed}
                >
                  {#if isResolved}
                    <CheckCircle2
                      class="h-3.5 w-3.5 shrink-0"
                      style="color: {severityVar(m.ev.severity)}"
                      aria-hidden="true"
                    />
                  {:else if isSuppressed}
                    <BellOff
                      class="h-3.5 w-3.5 shrink-0 text-muted-foreground"
                      aria-hidden="true"
                    />
                  {:else}
                    <Bell
                      class="h-3.5 w-3.5 shrink-0"
                      style="color: {severityVar(m.ev.severity)}"
                      aria-hidden="true"
                    />
                  {/if}
                  <span
                    class="font-mono text-xs text-muted-foreground tabular-nums w-16 shrink-0"
                  >
                    {formatTime(m.ev.at)}
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
          <div class="min-h-0 overflow-y-auto">
            <RuleSidebar
              rules={allRules}
              {editingRuleId}
              {treeByRule}
              {leafIdsByRule}
              {leafLog}
              {factLog}
              {currentTimeMs}
              bind:disabledRuleIds
              availableRules={allRules
                .filter((r) => r.id)
                .map((r) => ({ id: r.id as string, name: r.name ?? "" }))}
            />
          </div>
        {/if}
      </div>
    {/if}

    <div
      class="flex items-start gap-2 rounded-md border bg-muted/30 p-3 text-xs text-muted-foreground"
    >
      <Info class="h-4 w-4 mt-0.5 flex-none" />
      <p>User snooze actions cannot be replayed.</p>
    </div>
  {/if}
</div>
