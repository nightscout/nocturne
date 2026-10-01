<script lang="ts">
  import { untrack } from "svelte";
  import {
    type DateValue,
    getLocalTimeZone,
    parseDate,
    today,
  } from "@internationalized/date";
  import { timeDay } from "d3-time";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import GlucoseCalendarPicker from "./GlucoseCalendarPicker.svelte";
  import * as Popover from "$lib/components/ui/popover";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Info from "@lucide/svelte/icons/info";
  import AlertCircle from "@lucide/svelte/icons/circle-alert";
  import CalendarIcon from "@lucide/svelte/icons/calendar";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import {
    replay,
    replayDryRun,
  } from "$api/generated/alertReplays.generated.remote";
  import { getRules } from "$api/generated/alertRules.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import type {
    AlertReplayResult,
    AlertRuleResponse,
    ReplayRuleDefinition,
  } from "$api-clients";
  import { formatRange } from "./alertTime";
  import { formatMediumDate } from "$lib/utils/formatting";
  import { createChartDataEngine } from "$lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte";
  import ReplayView from "./ReplayView.svelte";
  import { openDayInReview } from "$lib/components/dashboard/glucose-chart/day-in-review";
  import type { ConditionNode } from "./types";

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

    const day = selectedDate ? selectedDate.toDate(getLocalTimeZone()) : new Date();
    const at = ([hours, minutes]: [number, number]) =>
      new Date(day.getFullYear(), day.getMonth(), day.getDate(), hours, minutes);

    const from = at(fromHm);
    const to = at(toHm);
    return { from, to: to.getTime() <= from.getTime() ? timeDay.offset(to, 1) : to };
  }

  // Refreshed on every run, so the sidebar sees rules created since the parent loaded.
  let allRules = $state<AlertRuleResponse[]>([]);

  function dateLabel(d: DateValue | undefined): string {
    if (!d) return "Last 24 hours";
    return formatMediumDate(d.toDate(getLocalTimeZone()));
  }

  // Link to the Day in Review report for the day under replay. Prefers the
  // explicitly picked date; falls back to the day of the result window so the
  // link still works for a rolling last-24h / brushed window.
  let dayInReviewHref = $derived.by<string | undefined>(() => {
    let ymd: string | undefined;
    if (selectedDate) {
      ymd = selectedDate.toString();
    } else if (result?.windowStart) {
      const d = new Date(result.windowStart);
      if (!Number.isNaN(d.getTime())) {
        const pad = (n: number) => String(n).padStart(2, "0");
        ymd = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
      }
    }
    return ymd ? `/reports/day-in-review?date=${ymd}` : undefined;
  });

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
    try {
      const range = computeRange();
      const date = !range && selectedDate ? selectedDate.toString() : undefined;
      const replayResult = rule
        ? await replayDryRun({
            date,
            timezone: browserTimezone,
            from: range?.from.toISOString(),
            to: range?.to.toISOString(),
            rule: typeof rule === "function" ? rule() : rule,
          })
        : await replay({
            date,
            timezone: browserTimezone,
            from: range?.from.toISOString(),
            to: range?.to.toISOString(),
          });

      // Falls back to the seeded availableRules prop on error.
      let rulesList: AlertRuleResponse[] = availableRules;
      try {
        const fresh = await getRules().run();
        if (fresh && fresh.length > 0) rulesList = fresh;
      } catch {
        // Fall through to the seed list.
      }
      allRules = rulesList;
      result = replayResult ?? null;
    } catch (err) {
      runError = describeSubmitError(err, "Failed to run replay. Please try again.");
    } finally {
      running = false;
    }
  }

  let hasRun = $derived(result !== null);

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
</script>

<div
  class="@container flex h-full min-h-0 flex-col gap-4 overflow-y-auto @2xl:overflow-y-hidden"
>
  <div class="flex flex-wrap items-center gap-2">
    <Popover.Root bind:open={datePickerOpen}>
      <Popover.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button
            {...props}
            variant="combobox"
            size="sm"
            class="justify-start"
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
            size="xs"
            class="w-full justify-start"
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
      <Input
        type="time"
        size="sm"
        class="w-auto"
        bind:value={fromTime}
        oninput={() => (brushDomain = null)}
      />
    </label>
    <label class="flex items-center gap-1.5 text-xs text-muted-foreground">
      To
      <Input
        type="time"
        size="sm"
        class="w-auto"
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

    {#if dayInReviewHref}
      <Button
        variant="outline"
        href={dayInReviewHref}
        size="sm"
        class="ml-auto"
        title="Open Day in Review for this day"
      >
        <CalendarDays class="h-3.5 w-3.5 text-muted-foreground" />
        <span class="hidden @sm:inline">Day in review</span>
      </Button>
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

    <ReplayView
      {result}
      rules={allRules}
      {editingRuleId}
      {editingTree}
      onSelectionChange={handleBrushSelection}
      onTimeClick={openDayInReview}
      chartEngine={({ range, onDataReady }) =>
        createChartDataEngine({ dateRange: range, enablePredictions: false, onDataReady })}
    />

    <div
      class="flex items-start gap-2 rounded-md border bg-muted/30 p-3 text-xs text-muted-foreground"
    >
      <Info class="h-4 w-4 mt-0.5 flex-none" />
      <p>User snooze actions cannot be replayed.</p>
    </div>
  {/if}
</div>
