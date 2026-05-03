<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import { Loader2, Info, History, AlertCircle, PlayCircle } from "lucide-svelte";
  import { Chart, Svg, Spline, Area, Threshold, AnnotationRange } from "layerchart";
  import { curveMonotoneX } from "d3-shape";
  import { replay } from "$api/generated/alertReplays.generated.remote";
  import { getDashboardChartData } from "$api/generated/chartDatas.generated.remote";
  import { AlertRuleSeverity } from "$api-clients";
  import type { AlertReplayResult, AlertReplayEvent, AlertRuleResponse, GlucosePointDto } from "$api-clients";

  interface Props {
    open: boolean;
    /** Sibling rules — currently unused by the dialog but kept on the prop
     *  shape so the overview can pass the same list it shows in the table
     *  (future: per-rule overlay toggle). */
    availableRules?: AlertRuleResponse[];
  }

  let { open = $bindable(), availableRules = [] }: Props = $props();
  // Keep the prop referenced so TypeScript doesn't dead-code-warn it.
  void availableRules;

  // ---- Range presets ----
  // Each preset resolves on demand via `resolvePreset` so the "now" boundary
  // is always relative to the click moment, not when the dialog mounted.
  type Preset = "last24h" | "yesterday" | "7daysAgo" | "custom";
  let preset = $state<Preset>("last24h");
  let customDate = $state<string>(""); // YYYY-MM-DD

  const browserTimezone =
    typeof Intl !== "undefined"
      ? Intl.DateTimeFormat().resolvedOptions().timeZone
      : "UTC";
  let timezone = $state<string>(browserTimezone);

  let running = $state(false);
  let runError = $state<string | null>(null);
  let result = $state<AlertReplayResult | null>(null);
  let glucose = $state<GlucosePointDto[]>([]);

  // ---- Run ----

  /**
   * Resolve the active preset to the (date, scope) the backend understands.
   * `last24h` sends `null` so the API picks "now-24h..now"; date-bound
   * presets send a YYYY-MM-DD string the backend interprets as a 24-hour
   * window in the supplied timezone.
   */
  function resolvePreset(): { date: string | null } {
    switch (preset) {
      case "last24h":
        return { date: null };
      case "yesterday":
        return { date: ymd(daysAgo(1)) };
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
    try {
      const { date } = resolvePreset();
      const replayResult = await replay({
        date: date as unknown as Date | undefined,
        timezone: timezone || undefined,
      });
      result = replayResult ?? null;

      // Once the backend tells us the resolved window, fetch glucose for the
      // exact same range so the chart's x-axis lines up with the event
      // markers. Tolerant of a missing window — we just skip the chart.
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
          // Chart overlay is best-effort; the replay event list is the
          // authoritative answer to "would my rules have fired?"
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

  // ---- Display helpers ----

  function severityLabel(s: AlertRuleSeverity | string | undefined): string {
    switch (s) {
      case AlertRuleSeverity.Critical:
        return "Critical";
      case AlertRuleSeverity.Warning:
        return "Warning";
      case AlertRuleSeverity.Info:
        return "Info";
      default:
        return s ?? "";
    }
  }

  function severityClass(s: AlertRuleSeverity | string | undefined): string {
    switch (s) {
      case AlertRuleSeverity.Critical:
        return "bg-destructive text-destructive-foreground";
      case AlertRuleSeverity.Warning:
        return "bg-amber-500/15 text-amber-700 dark:text-amber-400 border border-amber-500/30";
      case AlertRuleSeverity.Info:
        return "bg-blue-500/15 text-blue-700 dark:text-blue-400 border border-blue-500/30";
      default:
        return "bg-muted text-muted-foreground";
    }
  }

  function severityStroke(s: AlertRuleSeverity | string | undefined): string {
    switch (s) {
      case AlertRuleSeverity.Critical:
        return "stroke-red-500";
      case AlertRuleSeverity.Warning:
        return "stroke-amber-500";
      case AlertRuleSeverity.Info:
        return "stroke-sky-500";
      default:
        return "stroke-muted-foreground";
    }
  }

  function formatTime(at: Date | string | undefined, tz: string): string {
    if (!at) return "";
    const d = at instanceof Date ? at : new Date(at);
    if (Number.isNaN(d.getTime())) return "";
    try {
      return new Intl.DateTimeFormat(undefined, {
        hour: "2-digit",
        minute: "2-digit",
        timeZone: tz || undefined,
      }).format(d);
    } catch {
      return d.toISOString();
    }
  }

  function formatRange(
    start: Date | string | undefined,
    end: Date | string | undefined,
    tz: string,
  ): string {
    const s = start ? new Date(start as string | Date) : null;
    const e = end ? new Date(end as string | Date) : null;
    if (!s || !e || Number.isNaN(s.getTime()) || Number.isNaN(e.getTime()))
      return "";
    try {
      const fmt = new Intl.DateTimeFormat(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        timeZone: tz || undefined,
      });
      return `${fmt.format(s)} — ${fmt.format(e)}`;
    } catch {
      return `${s.toISOString()} — ${e.toISOString()}`;
    }
  }

  // ---- Chart shape ----

  /**
   * Glucose readings reshaped for layerchart. `time` on the wire is unix
   * milliseconds; layerchart wants a Date on the x-axis. Sorted ascending
   * to keep the spline well-behaved when the backend returns out-of-order
   * (rare, but defensive).
   */
  let chartData = $derived(
    glucose
      .filter((g) => g.time != null && g.sgv != null)
      .map((g) => ({ date: new Date(g.time as number), value: g.sgv as number }))
      .sort((a, b) => a.date.getTime() - b.date.getTime()),
  );

  let xDomain = $derived.by<[Date, Date] | undefined>(() => {
    if (!result?.windowStart || !result?.windowEnd) return undefined;
    return [new Date(result.windowStart), new Date(result.windowEnd)];
  });

  // Y bounds — clamp to a sensible glucose viewport that covers the readings
  // and the standard 70/180 range. Avoids autosizing to a single outlier.
  const Y_FLOOR = 40;
  const Y_CEIL = 350;
  const LOW = 70;
  const HIGH = 180;
  let yDomain = $derived<[number, number]>(() => {
    if (chartData.length === 0) return [Y_FLOOR, Y_CEIL];
    const max = Math.max(Y_CEIL - 50, ...chartData.map((p) => p.value));
    const min = Math.min(Y_FLOOR + 10, ...chartData.map((p) => p.value));
    return [Math.max(Y_FLOOR, min - 10), Math.min(Y_CEIL, max + 10)];
  });

  // Event markers projected into the same x-domain for the overlay. Skipping
  // those whose timestamp falls outside the resolved window (shouldn't
  // happen but defensive).
  let markers = $derived.by(() => {
    if (!xDomain) return [];
    const [start, end] = xDomain;
    const startMs = start.getTime();
    const endMs = end.getTime();
    return (result?.events ?? [])
      .map((ev) => {
        const t = ev.at ? new Date(ev.at).getTime() : NaN;
        if (!Number.isFinite(t) || t < startMs || t > endMs) return null;
        const xPct = ((t - startMs) / (endMs - startMs)) * 100;
        return { ev, xPct };
      })
      .filter((m): m is { ev: AlertReplayEvent; xPct: number } => m !== null);
  });

  let groupedEvents = $derived.by(() => {
    const events = result?.events ?? [];
    const tz = timezone || browserTimezone;
    const groups: { label: string; events: AlertReplayEvent[] }[] = [];
    let currentLabel: string | null = null;
    for (const ev of events) {
      const label = ev.at
        ? new Intl.DateTimeFormat(undefined, {
            weekday: "short",
            month: "short",
            day: "numeric",
            hour: "2-digit",
            timeZone: tz || undefined,
          }).format(new Date(ev.at))
        : "";
      if (label !== currentLabel) {
        groups.push({ label, events: [] });
        currentLabel = label;
      }
      groups[groups.length - 1].events.push(ev);
    }
    return groups;
  });

  let hasRun = $derived(result !== null);
  let isEmpty = $derived(hasRun && (result?.events?.length ?? 0) === 0);
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-3xl">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <History class="h-4 w-4" />
        Simulate alert rules
      </Dialog.Title>
      <Dialog.Description>
        Replay your enabled rules against historical glucose. Nothing is delivered.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-2">
      <!-- Preset chips + custom date -->
      <div class="flex flex-wrap items-center gap-2">
        {@render presetChip("last24h", "Last 24h")}
        {@render presetChip("yesterday", "Yesterday")}
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
        <div class="flex items-center gap-1.5">
          <Label for="replay-tz" class="text-xs text-muted-foreground">TZ</Label>
          <Input
            id="replay-tz"
            type="text"
            class="h-8 w-44 text-sm"
            bind:value={timezone}
            placeholder={browserTimezone}
          />
        </div>
        <Button onclick={handleRun} disabled={running || (preset === "custom" && !customDate)}>
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
        <!-- Window range -->
        {#if result.windowStart && result.windowEnd}
          <p class="text-xs text-muted-foreground">
            Window: {formatRange(
              result.windowStart,
              result.windowEnd,
              timezone || browserTimezone,
            )}
          </p>
        {/if}

        <!-- Glucose curve overlay with fire markers -->
        {#if xDomain && chartData.length > 0}
          <div class="relative h-48 w-full rounded-md border bg-background">
            <Chart
              data={chartData}
              x="date"
              y="value"
              {xDomain}
              yDomain={yDomain()}
              padding={{ top: 6, bottom: 6, left: 6, right: 6 }}
            >
              <Svg>
                <AnnotationRange
                  y={[LOW, HIGH]}
                  class="fill-emerald-500 opacity-10"
                />
                <Threshold curve={curveMonotoneX}>
                  {#snippet above()}
                    <Area
                      y0={HIGH}
                      curve={curveMonotoneX}
                      class="fill-violet-500/30"
                      line={{ class: "stroke-none" }}
                    />
                  {/snippet}
                  {#snippet below()}
                    <Area
                      y0={LOW}
                      curve={curveMonotoneX}
                      class="fill-orange-500/30"
                      line={{ class: "stroke-none" }}
                    />
                  {/snippet}
                  <Spline
                    curve={curveMonotoneX}
                    class="stroke-foreground/70 stroke-[1.5] fill-none"
                  />
                </Threshold>
              </Svg>
            </Chart>
            <!-- Fire markers: absolute-positioned vertical lines so they sit
                 above the chart without forcing a re-mount on event change.
                 Positioned by the precomputed `xPct` (percentage from window
                 start), which already aligns 1:1 with the chart's xDomain. -->
            {#each markers as m, i (i)}
              <span
                class="pointer-events-none absolute top-1 bottom-1 w-px {severityStroke(m.ev.severity).replace('stroke-', 'bg-')}"
                style:left="{m.xPct}%"
                aria-hidden="true"
                title="{m.ev.ruleName ?? ''} · {formatTime(m.ev.at, timezone || browserTimezone)}"
              ></span>
            {/each}
          </div>
        {:else if isEmpty && xDomain}
          <div class="flex h-32 w-full items-center justify-center rounded-md border border-dashed text-sm text-muted-foreground">
            No glucose data in this window.
          </div>
        {/if}

        {#if isEmpty}
          <div
            class="rounded-md border bg-muted/30 px-4 py-6 text-center text-sm text-muted-foreground"
          >
            No events would have fired in this window.
          </div>
        {:else}
          <div class="max-h-72 overflow-y-auto rounded-md border divide-y">
            {#each groupedEvents as group, gi (gi)}
              <div class="bg-muted/20 px-3 py-1.5 text-xs font-medium text-muted-foreground sticky top-0">
                {group.label}
              </div>
              {#each group.events as ev, ei (gi + ":" + ei)}
                <div class="flex items-center gap-3 px-3 py-2 text-sm">
                  <span class="font-mono text-xs text-muted-foreground tabular-nums w-16 shrink-0">
                    {formatTime(ev.at, timezone || browserTimezone)}
                  </span>
                  <Badge class={severityClass(ev.severity)}>
                    {severityLabel(ev.severity)}
                  </Badge>
                  <span class="flex-1 min-w-0 truncate">
                    {ev.ruleName ?? "(unnamed rule)"}
                  </span>
                </div>
              {/each}
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

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (open = false)}>Close</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

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
