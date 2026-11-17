<!--
  The Daily profile page body: every series on one local time-of-day axis, stacked so a single crosshair
  reads them all at once. Values come from the API as they are; the body picks only the axes, splits
  series where they are absent or change sparseness, and lays the panels out.
-->
<script lang="ts">
  import { Area, Rule } from "layerchart";
  import { curveStepAfter } from "d3-shape";
  import {
    IobCobSource,
    LoopAnalysisSeries,
    type DailyProfileResponse,
    type DailyProfileSlot,
    type LoopAnalysisAggregate,
  } from "#lib/api/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import FigureStrip, { type Figure } from "#lib/components/reports/FigureStrip.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { CHART_TEXTURES, dashClass, patternClass } from "#lib/components/charts/print/chart-print-patterns.js";
  import {
    BASAL_BANDS,
    COB_BANDS as COB,
    GLUCOSE_BANDS,
    IOB_BANDS as IOB,
  } from "#lib/components/reports/percentile-band-colors.js";
  import { plural } from "#lib/utils/plural.js";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import { tenantChartThresholds } from "#lib/stores/realtime-store.svelte.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { bg, bgLabel, convertToDisplayUnits, hourLabel } from "#lib/utils/formatting.js";
  import EyeOff from "@lucide/svelte/icons/eye-off";
  import TimeOfDayPanel from "./TimeOfDayPanel.svelte";
  import PercentileBands from "./PercentileBands.svelte";
  import SlotLine from "./SlotLine.svelte";
  import SlotBars, { type SlotBar } from "./SlotBars.svelte";
  import AggregateReadout from "./AggregateReadout.svelte";
  import SlotDays from "./SlotDays.svelte";
  import { TimeOfDayCursor } from "./time-of-day-cursor.svelte";
  import { loopAnalysisMark } from "./loop-analysis-tour.svelte";
  import { atMinute, axisEnd, axisStart, signedTwoPlaces as signed } from "./daily-profile-view";
  import NoTimeZone from "#lib/components/reports/NoTimeZone.svelte";
  import { loopAnalysisTimeZoneNeed } from "./time-zone-need.svelte";
  import IgnoredFiltersNotice from "./IgnoredFiltersNotice.svelte";
  import HistoryClampNote from "./HistoryClampNote.svelte";

  interface Props {
    response: DailyProfileResponse;
    /** Insulin Delivery over the same range, for its whole-range figures. */
    insulinDeliveryHref?: string | null;
  }

  let { response, insulinDeliveryHref = null }: Props = $props();

  const primary = $derived(response.primary);
  const comparisonRange = $derived(response.comparison ?? null);
  const slots = $derived(primary?.slots ?? []);
  const comparisonSlots = $derived(comparisonRange?.slots ?? null);
  const comparing = $derived(comparisonSlots != null);
  const hasDays = $derived((primary?.days?.length ?? 0) > 0);

  const cursor = new TimeOfDayCursor();
  let showManualTemp = $state(false);
  let showSuspend = $state(false);
  let showInferred = $state(false);

  const units = $derived(glucoseUnits.current);
  const toDisplay = $derived((mgdl: number) => convertToDisplayUnits(mgdl, units));
  const thresholds = tenantChartThresholds();

  const at = (list: DailyProfileSlot[] | null | undefined, slot: number) =>
    list?.[slot]?.slot === slot ? list[slot] : list?.find((s) => s.slot === slot);

  const has = (pick: (s: DailyProfileSlot) => unknown) => slots.some((s) => pick(s) != null);

  const both = $derived([...slots, ...(comparisonSlots ?? [])]);

  const glucoseDomain = $derived<[number, number]>([0, toDisplay(thresholds.current.glucoseYMax)]);
  const basalDomain = $derived<[number, number]>([
    0,
    axisEnd(both.flatMap((s) => [s.deliveredBasal?.p90, s.scheduledBasal?.p50]), 0.5),
  ]);
  const iobDomain = $derived<[number, number]>([
    axisStart(both.map((s) => s.iob?.p10)),
    axisEnd(both.map((s) => s.iob?.p90), 1),
  ]);
  const cobDomain = $derived<[number, number]>([0, axisEnd(both.map((s) => s.cob?.p90), 10)]);

  const correctionValues = $derived(
    both.flatMap((s) => [
      s.netLoopCorrection?.mean,
      s.netLoopCorrection?.rate?.p25,
      s.netLoopCorrection?.rate?.p75,
      showManualTemp ? s.manualTemp?.mean : null,
      showSuspend ? s.suspend?.mean : null,
      showInferred ? s.inferred?.mean : null,
    ])
  );
  const correctionDomain = $derived<[number, number]>([
    -axisEnd(correctionValues.map((v) => (v == null ? null : -v)), 0.25),
    axisEnd(correctionValues, 0.25),
  ]);

  const hourlyBars = (range: typeof primary | null): SlotBar[] =>
    (range?.hourlyNetLoopCorrection ?? [])
      .filter((h) => h.mean != null)
      .map((h) => ({ start: (h.hour ?? 0) * 12, end: (h.hour ?? 0) * 12 + 12, value: h.mean!, sparse: h.sparse }));
  const hourlyPrimary = $derived(hourlyBars(primary));
  const hourlyComparison = $derived(comparisonRange ? hourlyBars(comparisonRange) : []);
  const hourlyDomain = $derived<[number, number]>([
    -axisEnd([...hourlyPrimary, ...hourlyComparison].map((b) => -b.value), 0.1),
    axisEnd([...hourlyPrimary, ...hourlyComparison].map((b) => b.value), 0.1),
  ]);
  const hourOf = (slot: number) => Math.floor(slot / 12);

  const eventBars = (list: DailyProfileSlot[] | null, pick: (s: DailyProfileSlot) => DailyProfileSlot["carbs"]): SlotBar[] =>
    (list ?? [])
      .filter((s) => (pick(s)?.meanPerDay ?? 0) !== 0)
      .map((s) => ({ start: s.slot ?? 0, end: (s.slot ?? 0) + 1, value: pick(s)!.meanPerDay!, sparse: pick(s)?.sparse }));
  const bolusBars = $derived(eventBars(slots, (s) => s.manualBoluses));
  const bolusComparison = $derived(eventBars(comparisonSlots, (s) => s.manualBoluses));
  const carbBars = $derived(eventBars(slots, (s) => s.carbs));
  const carbComparison = $derived(eventBars(comparisonSlots, (s) => s.carbs));
  const bolusDomain = $derived<[number, number]>([0, axisEnd([...bolusBars, ...bolusComparison].map((b) => b.value), 0.1)]);
  const carbDomain = $derived<[number, number]>([0, axisEnd([...carbBars, ...carbComparison].map((b) => b.value), 1)]);

  const withheld = $derived(new Set(response.withheldSeries ?? []));
  const hidden = (series: LoopAnalysisSeries) => withheld.has(series);
  const componentsHidden = $derived(withheld.has(LoopAnalysisSeries.NetLoopCorrectionComponents));
  const hiddenText = () => "Hidden: your access does not include this data";

  /** What a panel shows in place of its plot: why it is hidden, or that there is nothing to draw. */
  const emptyText = (series: LoopAnalysisSeries, available: boolean, none: string) =>
    hidden(series) ? hiddenText() : available ? null : none;

  const targetRuns = $derived.by(() => {
    const runs: DailyProfileSlot[][] = [];
    let run: DailyProfileSlot[] = [];
    for (const s of slots) {
      if (s.targetLow != null && s.targetHigh != null) {
        run.push(s);
      } else if (run.length) {
        runs.push(run);
        run = [];
      }
    }
    if (run.length) runs.push(run);
    return runs.map((r) => [...r, { ...r[r.length - 1], slot: (r[r.length - 1].slot ?? 0) + 1 }]);
  });

  const u2 = (v: number) => v.toFixed(2);
  const u1 = (v: number) => v.toFixed(1);
  const glucoseText = (v: number) => String(bg(v));
  const axisGlucose = (v: number) => (units === "mmol" ? v.toFixed(0) : String(Math.round(v)));

  const COMPONENT_COLORS = {
    manualTemp: "var(--chart-2)",
    suspend: "var(--chart-3)",
    inferred: "var(--chart-5)",
  };
  const CORRECTION_LINE = "var(--insulin)";
  const CORRECTION_BAND = "color-mix(in oklab, var(--insulin) 28%, transparent)";
  const SCHEDULED = "var(--foreground)";
  const TARGET_EDGE = CHART_TEXTURES["target-range-limit"].color;

  const summary = $derived(primary?.summary);
  const percent = (v: number | undefined) => `${(v ?? 0).toFixed(0)}%`;
  const hiddenFigure = (label: string): Figure => ({ label, value: "–", note: hiddenText() });

  const figures = $derived.by<Figure[]>(() => {
    const days = summary?.daysWithData ?? 0;
    const list: Figure[] = [
      { label: "Days with data", value: String(days), unit: plural(days, ["day", "days"]) },
    ];

    const insulin = summary?.insulin;
    if (hidden(LoopAnalysisSeries.InsulinSummary)) {
      list.push(hiddenFigure("Total insulin"));
    } else if (insulin) {
      list.push(
        {
          label: "Total insulin",
          value: u1(insulin.total?.mean ?? 0),
          unit: "U/day",
          note: `Median ${u1(insulin.total?.p50 ?? 0)}, ${plural(insulin.total?.n ?? 0, ["over # day", "over # days"])}`,
        },
        {
          label: "Basal / bolus",
          value: `${percent(insulin.deliveredBasalPercent)} / ${percent(insulin.bolusPercent)}`,
          note: `${u1(insulin.deliveredBasal ?? 0)} / ${u1(insulin.bolus ?? 0)} U/day`,
        },
        {
          label: "Automatic / manual bolus",
          value: `${u1(insulin.automaticBolus ?? 0)} / ${u1(insulin.manualBolus ?? 0)}`,
          unit: "U/day",
          note: `${percent(insulin.automaticBolusPercent)} / ${percent(insulin.manualBolusPercent)} of total insulin`,
        }
      );
    }

    if (hidden(LoopAnalysisSeries.DeliveredToScheduledRatio))
      list.push(hiddenFigure("Delivered basal ratio"));
    else if (summary?.deliveredToScheduledRatio != null)
      list.push({
        label: "Delivered basal ratio",
        value: `${summary.deliveredToScheduledRatio.toFixed(2)}×`,
        note: "Delivered basal divided by scheduled basal",
      });

    if (hidden(LoopAnalysisSeries.NetLoopCorrectionSummary)) {
      list.push(hiddenFigure("Net loop correction"));
    } else if (summary?.netLoopCorrectionPerDay != null) {
      const correctionDays = summary.netLoopCorrectionDays ?? 0;
      list.push({
        label: "Net loop correction",
        value: signed(summary.netLoopCorrectionPerDay),
        unit: "U/day",
        note:
          summary.netLoopCorrectionTddPercent != null
            ? `${summary.netLoopCorrectionTddPercent.toFixed(0)}% of total insulin, ${plural(correctionDays, ["over # day", "over # days"])}`
            : plural(correctionDays, ["Over # day", "Over # days"]),
      });
    }
    return list;
  });

  const daysText = (n: number | undefined) => plural(n ?? 0, ["# day", "# days"]);
  const readingsText = (n: number | undefined) => plural(n ?? 0, ["# reading", "# readings"]);

  /** A slot's headline and n, as the slider reads them out. */
  function describeSpread(
    value: LoopAnalysisAggregate | null | undefined,
    format: (v: number) => string,
    unit: string,
    counts: "days" | "readings"
  ): string {
    if (!value) return "No data at this time";
    const n = counts === "readings" ? readingsText(value.n) : daysText(value.n);
    return `Median ${format(value.p50 ?? 0)} ${unit}, ${n}`;
  }

  function describeEvents(series: DailyProfileSlot["carbs"], unit: string): string {
    if (!series) return "No data at this time";
    return `${u2(series.meanPerDay ?? 0)} ${unit} per day, ${daysText(series.n)}`;
  }

  function describeCorrection(slot: number): string {
    const value = at(slots, slot)?.netLoopCorrection;
    if (!value) return "No data at this time";
    return `Net loop correction ${signed(value.mean ?? 0)} U/h, ${daysText(value.rate?.n)}`;
  }

  function describeHour(slot: number): string {
    const hour = primary?.hourlyNetLoopCorrection?.find((h) => h.hour === hourOf(slot));
    if (hour?.mean == null) return "No data in this hour";
    return `Net loop correction ${signed(hour.mean)} U, ${daysText(hour.n)}`;
  }

  const pickHelp = "Press Enter to list the days for the time. Press Escape to close the list.";

  function closeDays() {
    cursor.selected = null;
    cursor.restoreFocus();
  }
</script>

{#snippet eventReadout(series: DailyProfileSlot["carbs"], comparisonSeries: DailyProfileSlot["carbs"], unit: string)}
  {#if !series}
    <p class="text-muted-foreground">No data at this time</p>
  {:else}
    <dl class="grid grid-cols-[auto_auto] gap-x-3 tabular-nums">
      <dt class="text-muted-foreground">Mean per day</dt>
      <dd class="text-right">{u2(series.meanPerDay ?? 0)} {unit}</dd>
      <dt class="text-muted-foreground">Events per day</dt>
      <dd class="text-right">{u2(series.eventsPerDay ?? 0)}</dd>
      <dt class="text-muted-foreground">n</dt>
      <dd class="text-right">{daysText(series.n)}</dd>
      {#if comparing}
        <dt class="text-muted-foreground">Compared, mean per day</dt>
        <dd class="text-right">{comparisonSeries ? `${u2(comparisonSeries.meanPerDay ?? 0)} ${unit}` : "–"}</dd>
      {/if}
    </dl>
    {#if series.sparse}
      <p class="text-muted-foreground">Too few days to show a pattern</p>
    {/if}
  {/if}
{/snippet}

{#if response.timeZoneUnavailableReason}
  <NoTimeZone reason={response.timeZoneUnavailableReason}>{loopAnalysisTimeZoneNeed()}</NoTimeZone>
{:else}
  <div class="space-y-6">
    <HistoryClampNote {response}>Each time of day uses data from one day at most.</HistoryClampNote>

    <IgnoredFiltersNotice ignored={response.ignoredFilters} />

    <div class="space-y-2" data-testid="loop-analysis-daily-totals" {@attach loopAnalysisMark("loop-analysis-daily-profile.totals")}>
      <FigureStrip {figures} />
      {#if insulinDeliveryHref}
        <Button href={insulinDeliveryHref} variant="link" size="inline">
          Insulin Delivery shows the totals for the full range, without these filters
          <ArrowRight />
        </Button>
      {/if}
    </div>

    {#if !hasDays}
      <div class="rounded-lg border border-dashed p-8 text-center text-muted-foreground">
        <p class="font-medium">No data in this range</p>
        <p class="text-sm">Select a longer range, or use fewer filters.</p>
      </div>
    {:else}
      <div class="space-y-5 rounded-lg border bg-card p-3 @md:p-4" data-testid="loop-analysis-daily-curves">
        {#if comparing}
          <p class="text-xs text-muted-foreground">
            The charts show the comparison range as dashed outlines on each series.
          </p>
        {/if}

        <div {@attach loopAnalysisMark("loop-analysis-daily-profile.glucose")}>
          <TimeOfDayPanel
            id="glucose"
            describe={(slot) => describeSpread(at(slots, slot)?.glucose, glucoseText, bgLabel(), "readings")}
            title="Glucose"
            unit={bgLabel()}
            {cursor}
            {pickHelp}
            yDomain={glucoseDomain}
            yFormat={axisGlucose}
            heightClass="h-48"
            empty={emptyText(LoopAnalysisSeries.Glucose, has((s) => s.glucose), "No glucose readings in this range")}
            legend={[
              { texture: "percentile-outer", label: "10–90%", color: GLUCOSE_BANDS.outer },
              { texture: "percentile-inner", label: "25–75%", color: GLUCOSE_BANDS.inner },
              { texture: "percentile-median", label: "Median", color: GLUCOSE_BANDS.median, shape: "line" },
              ...(hidden(LoopAnalysisSeries.Target) ? [] : [{ texture: "target-band" as const, label: "Active target range" }]),
            ]}
            hiddenNote={hidden(LoopAnalysisSeries.Target) ? "Target range hidden: your access does not include it" : null}
          >
            {#snippet marks()}
              {#each targetRuns as run, i (i)}
                <Area
                  data={run}
                  x={(s: DailyProfileSlot) => s.slot ?? 0}
                  y0={(s: DailyProfileSlot) => toDisplay(s.targetLow ?? 0)}
                  y1={(s: DailyProfileSlot) => toDisplay(s.targetHigh ?? 0)}
                  curve={curveStepAfter}
                  fill={CHART_TEXTURES["target-band"].color}
                  class={patternClass("target-band")}
                />
              {/each}
              <!-- A single-value target has no height as a band, so its edges are drawn as well. -->
              <SlotLine {slots} value={(s) => (s.targetLow == null ? null : toDisplay(s.targetLow))} stroke={TARGET_EDGE} dash="4 4" width={1} step />
              <SlotLine {slots} value={(s) => (s.targetHigh == null ? null : toDisplay(s.targetHigh))} stroke={TARGET_EDGE} dash="4 4" width={1} step />
              <PercentileBands
                {slots}
                comparison={comparisonSlots}
                pick={(s) => s.glucose}
                scale={toDisplay}
                outerColor={GLUCOSE_BANDS.outer}
                innerColor={GLUCOSE_BANDS.inner}
                lineColor={GLUCOSE_BANDS.median}
              />
            {/snippet}
            {#snippet tooltip(slot)}
              {@const s = at(slots, slot)}
              <AggregateReadout
                value={s?.glucose}
                comparison={at(comparisonSlots, slot)?.glucose}
                showComparison={comparing}
                format={glucoseText}
                counts="readings"
              />
              {#if s?.targetLow != null && s.targetHigh != null}
                <p class="mt-1 text-muted-foreground">Target {bg(s.targetLow)}–{bg(s.targetHigh)}</p>
              {/if}
            {/snippet}
          </TimeOfDayPanel>
        </div>

        <TimeOfDayPanel
          id="basal"
          describe={(slot) => describeSpread(at(slots, slot)?.deliveredBasal, u2, "U/h", "days")}
          title="Basal"
          unit="U/h"
          {cursor}
          {pickHelp}
          yDomain={basalDomain}
          empty={emptyText(
            LoopAnalysisSeries.DeliveredBasal,
            has((s) => s.deliveredBasal) || has((s) => s.scheduledBasal),
            "No basal delivery in this range"
          )}
          hiddenNote={hidden(LoopAnalysisSeries.ScheduledBasal) ? "Scheduled basal hidden: your access does not include it" : null}
          legend={[
            { texture: "percentile-outer", label: "Delivered 10–90%", color: BASAL_BANDS.outer },
            { texture: "percentile-inner", label: "Delivered 25–75%", color: BASAL_BANDS.inner },
            { texture: "percentile-median", label: "Delivered median", color: BASAL_BANDS.median, shape: "line" },
            ...(hidden(LoopAnalysisSeries.ScheduledBasal)
              ? []
              : [{ texture: "insulin-scheduled-basal" as const, label: "Scheduled", color: SCHEDULED, shape: "line" as const }]),
          ]}
        >
          {#snippet marks()}
            <PercentileBands
              {slots}
              comparison={comparisonSlots}
              pick={(s) => s.deliveredBasal}
              outerColor={BASAL_BANDS.outer}
              innerColor={BASAL_BANDS.inner}
              lineColor={BASAL_BANDS.median}
            />
            {#if comparisonSlots}
              <SlotLine slots={comparisonSlots} value={(s) => s.scheduledBasal?.p50} stroke={SCHEDULED} step comparison />
            {/if}
            <SlotLine
              {slots}
              value={(s) => s.scheduledBasal?.p50}
              sparse={(s) => s.scheduledBasal?.sparse ?? false}
              stroke={SCHEDULED}
              width={1.5}
              step
              class={dashClass("insulin-scheduled-basal")}
            />
          {/snippet}
          {#snippet tooltip(slot)}
            {@const s = at(slots, slot)}
            <AggregateReadout
              label="Delivered"
              value={s?.deliveredBasal}
              comparison={at(comparisonSlots, slot)?.deliveredBasal}
              showComparison={comparing}
              format={u2}
              counts="days"
            />
            {#if s?.scheduledBasal}
              <p class="mt-1 tabular-nums">
                <span class="text-muted-foreground">Scheduled median</span>
                {u2(s.scheduledBasal.p50 ?? 0)} U/h
              </p>
            {/if}
          {/snippet}
        </TimeOfDayPanel>

        <div class="space-y-2" data-testid="loop-analysis-net-correction" {@attach loopAnalysisMark("loop-analysis-daily-profile.correction")}>
          <TimeOfDayPanel
            id="correction"
            describe={describeCorrection}
            title="Net loop correction"
            unit="U/h above schedule"
            {cursor}
            {pickHelp}
            yDomain={correctionDomain}
            empty={emptyText(LoopAnalysisSeries.NetLoopCorrection, has((s) => s.netLoopCorrection), "No loop corrections in this range")}
            legend={[
              { texture: "percentile-inner", label: "Temp basal change 25–75%", color: CORRECTION_BAND },
              { texture: "percentile-median", label: "Mean, with automatic boluses", color: CORRECTION_LINE, shape: "line" },
              ...(showManualTemp ? [{ texture: "steps" as const, label: "Manual temp", color: COMPONENT_COLORS.manualTemp, shape: "line" as const }] : []),
              ...(showSuspend ? [{ texture: "steps" as const, label: "Suspend", color: COMPONENT_COLORS.suspend, shape: "line" as const }] : []),
              ...(showInferred ? [{ texture: "steps" as const, label: "Inferred", color: COMPONENT_COLORS.inferred, shape: "line" as const }] : []),
            ]}
          >
            {#snippet marks()}
              <Rule y={0} class="stroke-foreground/40" />
              <PercentileBands
                {slots}
                comparison={comparisonSlots}
                pick={(s) => s.netLoopCorrection?.rate}
                outer={false}
                centre={null}
                outerColor={CORRECTION_BAND}
                innerColor={CORRECTION_BAND}
              />
              {#if comparisonSlots}
                <SlotLine slots={comparisonSlots} value={(s) => s.netLoopCorrection?.mean} stroke={CORRECTION_LINE} comparison />
              {/if}
              <SlotLine
                {slots}
                value={(s) => s.netLoopCorrection?.mean}
                sparse={(s) => s.netLoopCorrection?.rate?.sparse ?? false}
                stroke={CORRECTION_LINE}
              />
              {#if showManualTemp}
                <SlotLine {slots} value={(s) => s.manualTemp?.mean} sparse={(s) => s.manualTemp?.sparse ?? false} stroke={COMPONENT_COLORS.manualTemp} width={1.5} />
              {/if}
              {#if showSuspend}
                <SlotLine {slots} value={(s) => s.suspend?.mean} sparse={(s) => s.suspend?.sparse ?? false} stroke={COMPONENT_COLORS.suspend} width={1.5} />
              {/if}
              {#if showInferred}
                <SlotLine {slots} value={(s) => s.inferred?.mean} sparse={(s) => s.inferred?.sparse ?? false} stroke={COMPONENT_COLORS.inferred} width={1.5} />
              {/if}
            {/snippet}
            {#snippet tooltip(slot)}
              {@const s = at(slots, slot)}
              {@const c = at(comparisonSlots, slot)}
              <AggregateReadout
                label="Temp basal change"
                value={s?.netLoopCorrection?.rate}
                comparison={c?.netLoopCorrection?.rate}
                showComparison={comparing}
                format={signed}
                counts="days"
                headline={{ label: "Net loop correction", value: s?.netLoopCorrection?.mean, comparison: c?.netLoopCorrection?.mean }}
              />
              {#if s?.netLoopCorrection}
                <p class="mt-1 tabular-nums">
                  <span class="text-muted-foreground">Automatic boluses, mean</span>
                  {signed(s.netLoopCorrection.automaticBolusMean ?? 0)} U/h
                </p>
              {/if}
              {#if showManualTemp && s?.manualTemp}
                <p class="tabular-nums"><span class="text-muted-foreground">Manual temp, mean</span> {signed(s.manualTemp.mean ?? 0)} U/h</p>
              {/if}
              {#if showSuspend && s?.suspend}
                <p class="tabular-nums"><span class="text-muted-foreground">Suspend, mean</span> {signed(s.suspend.mean ?? 0)} U/h</p>
              {/if}
              {#if showInferred && s?.inferred}
                <p class="tabular-nums"><span class="text-muted-foreground">Inferred, mean</span> {signed(s.inferred.mean ?? 0)} U/h</p>
              {/if}
            {/snippet}
          </TimeOfDayPanel>
          <div class="flex flex-wrap items-center gap-x-5 gap-y-2 px-1 text-sm print:hidden">
            <span class="text-xs text-muted-foreground">Also show</span>
            {#if componentsHidden}
              <span class="text-xs text-muted-foreground">{hiddenText()}</span>
            {/if}
            <div class="flex items-center gap-2">
              <Switch id="show-manual-temp" disabled={componentsHidden} bind:checked={showManualTemp} />
              <Label for="show-manual-temp">Manual temp</Label>
            </div>
            <div class="flex items-center gap-2">
              <Switch id="show-suspend" disabled={componentsHidden} bind:checked={showSuspend} />
              <Label for="show-suspend">Suspend</Label>
            </div>
            <div class="flex items-center gap-2">
              <Switch id="show-inferred" disabled={componentsHidden} bind:checked={showInferred} />
              <Label for="show-inferred">Inferred</Label>
            </div>
          </div>
        </div>

        <TimeOfDayPanel
          id="hourly-correction"
          describe={describeHour}
          title="Net loop correction by hour"
          unit="U per hour"
          {cursor}
          {pickHelp}
          yDomain={hourlyDomain}
          heightClass="h-28"
          empty={emptyText(LoopAnalysisSeries.HourlyNetLoopCorrection, hourlyPrimary.length > 0, "No loop corrections in this range")}
        >
          {#snippet marks()}
            <Rule y={0} class="stroke-foreground/40" />
            <SlotBars bars={hourlyPrimary} comparison={hourlyComparison} color={CORRECTION_LINE} />
          {/snippet}
          {#snippet tooltip(slot)}
            {@const hour = hourOf(slot)}
            {@const h = primary?.hourlyNetLoopCorrection?.find((x) => x.hour === hour)}
            {@const c = comparisonRange?.hourlyNetLoopCorrection?.find((x) => x.hour === hour)}
            <p class="text-muted-foreground">{hourLabel(atMinute(hour * 60))}–{hourLabel(atMinute(hour * 60 + 60))}</p>
            <dl class="grid grid-cols-[auto_auto] gap-x-3 tabular-nums">
              <dt class="text-muted-foreground">Mean</dt>
              <dd class="text-right">{h?.mean != null ? `${signed(h.mean)} U` : "–"}</dd>
              <dt class="text-muted-foreground">n</dt>
              <dd class="text-right">{daysText(h?.n)}</dd>
              {#if comparing}
                <dt class="text-muted-foreground">Compared</dt>
                <dd class="text-right">{c?.mean != null ? `${signed(c.mean)} U` : "–"}</dd>
              {/if}
            </dl>
            {#if h?.sparse}
              <p class="text-muted-foreground">Too few days to show a pattern</p>
            {/if}
          {/snippet}
        </TimeOfDayPanel>

        <TimeOfDayPanel
          id="iob"
          describe={(slot) => describeSpread(at(slots, slot)?.iob, u2, "U", "days")}
          title="Insulin on board"
          unit="U"
          {cursor}
          {pickHelp}
          yDomain={iobDomain}
          empty={emptyText(LoopAnalysisSeries.Iob, has((s) => s.iob), "No IOB in this range")}
          legend={[
            { texture: "percentile-outer", label: "10–90%", color: IOB.outer },
            { texture: "percentile-inner", label: "25–75%", color: IOB.inner },
            { texture: "percentile-median", label: "Median", color: IOB.median, shape: "line" },
          ]}
        >
          {#snippet marks()}
            <PercentileBands {slots} comparison={comparisonSlots} pick={(s) => s.iob} outerColor={IOB.outer} innerColor={IOB.inner} lineColor={IOB.median} />
          {/snippet}
          {#snippet tooltip(slot)}
            {@const s = at(slots, slot)}
            <AggregateReadout value={s?.iob} comparison={at(comparisonSlots, slot)?.iob} showComparison={comparing} format={u2} counts="days" />
            {#if s?.iobSource}
              <p class="mt-1 text-muted-foreground">
                {#if s.iobSource === IobCobSource.Device}Mostly as the loop reported it{:else}Mostly calculated from doses{/if}
              </p>
            {/if}
          {/snippet}
        </TimeOfDayPanel>

        <TimeOfDayPanel
          id="cob"
          describe={(slot) => describeSpread(at(slots, slot)?.cob, u1, "g", "days")}
          title="Carbs on board"
          unit="g"
          {cursor}
          {pickHelp}
          yDomain={cobDomain}
          empty={emptyText(LoopAnalysisSeries.Cob, has((s) => s.cob), "No COB in this range")}
          legend={[
            { texture: "percentile-outer", label: "10–90%", color: COB.outer },
            { texture: "percentile-inner", label: "25–75%", color: COB.inner },
            { texture: "percentile-median", label: "Median", color: COB.median, shape: "line" },
          ]}
        >
          {#snippet marks()}
            <PercentileBands {slots} comparison={comparisonSlots} pick={(s) => s.cob} outerColor={COB.outer} innerColor={COB.inner} lineColor={COB.median} />
          {/snippet}
          {#snippet tooltip(slot)}
            {@const s = at(slots, slot)}
            <AggregateReadout value={s?.cob} comparison={at(comparisonSlots, slot)?.cob} showComparison={comparing} format={u1} counts="days" />
            {#if s?.cobSource}
              <p class="mt-1 text-muted-foreground">
                {#if s.cobSource === IobCobSource.Device}Mostly as the loop reported it{:else}Mostly calculated from carbs{/if}
              </p>
            {/if}
          {/snippet}
        </TimeOfDayPanel>

        <TimeOfDayPanel
          id="boluses"
          describe={(slot) => describeEvents(at(slots, slot)?.manualBoluses, "U")}
          title="Manual boluses"
          unit="U per day"
          {cursor}
          {pickHelp}
          yDomain={bolusDomain}
          heightClass="h-24"
          empty={emptyText(LoopAnalysisSeries.ManualBoluses, has((s) => s.manualBoluses), "No manual boluses in this range")}
        >
          {#snippet marks()}
            <SlotBars bars={bolusBars} comparison={bolusComparison} color="var(--insulin-bolus)" gap={0} />
          {/snippet}
          {#snippet tooltip(slot)}
            {@render eventReadout(at(slots, slot)?.manualBoluses, at(comparisonSlots, slot)?.manualBoluses, "U")}
          {/snippet}
        </TimeOfDayPanel>

        <TimeOfDayPanel
          id="carbs"
          describe={(slot) => describeEvents(at(slots, slot)?.carbs, "g")}
          title="Carbs"
          unit="Grams per day"
          {cursor}
          {pickHelp}
          yDomain={carbDomain}
          heightClass="h-28"
          showTimeAxis
          empty={emptyText(LoopAnalysisSeries.Carbs, has((s) => s.carbs), "No carbs in this range")}
        >
          {#snippet marks()}
            <SlotBars bars={carbBars} comparison={carbComparison} color="var(--carbs)" gap={0} />
          {/snippet}
          {#snippet tooltip(slot)}
            {@render eventReadout(at(slots, slot)?.carbs, at(comparisonSlots, slot)?.carbs, "g")}
          {/snippet}
        </TimeOfDayPanel>

        <p class="flex items-center gap-1.5 px-1 text-xs text-muted-foreground" {@attach loopAnalysisMark("loop-analysis-daily-profile.days")}>
          <EyeOff class="size-3.5" />
          Faded areas have too few days to show a pattern. To list the days for a time, select the
          time or press Enter on it. Use the arrow keys to move along the day and between charts. Use
          Home and End to go to the start and the end of the day.
        </p>
      </div>

      {#if cursor.selected != null}
        <SlotDays
          slot={cursor.selected}
          primary={primary}
          comparison={comparisonRange}
          focusOnPick={cursor.selectedByKeyboard}
          onclose={closeDays}
        />
      {/if}
    {/if}
  </div>
{/if}
