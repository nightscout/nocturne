<!--
  The Loop health page body: how the loop ran, as the API measured it, in tiles for the range and strips
  for each hour of the day, with the gaps it found listed and linked to Day in Review. A figure the
  API could not measure reads as unknown, never as zero.
-->
<script lang="ts">
  import { resolve } from "$app/paths";
  import { Rule } from "layerchart";
  import {
    LoopAnalysisSeries,
    LoopHealthUnknownReason,
    type LoopHealthGap,
    type LoopHealthRange,
    type LoopHealthResponse,
    type LoopHealthShare,
    type LoopHealthUnknown,
  } from "$lib/api";
  import FigureStrip, { type Figure } from "$lib/components/reports/FigureStrip.svelte";
  import { Button } from "$lib/components/ui/button";
  import { plural } from "$lib/utils/plural";
  import { startOfDay } from "$lib/utils/date-range";
  import { formatMediumDate, formatWeekdayDate, hourLabel, time } from "$lib/utils/formatting";
  import ArrowUpRight from "@lucide/svelte/icons/arrow-up-right";
  import EyeOff from "@lucide/svelte/icons/eye-off";
  import TimeOfDayPanel from "./TimeOfDayPanel.svelte";
  import HourBars from "./HourBars.svelte";
  import HourBands from "./HourBands.svelte";
  import StripKey, { type StripKeyItem } from "./StripKey.svelte";
  import NoTimeZone from "$lib/components/reports/NoTimeZone.svelte";
  import { loopAnalysisTimeZoneNeed } from "./time-zone-need.svelte";
  import IgnoredFiltersNotice from "./IgnoredFiltersNotice.svelte";
  import HistoryClampNote from "./HistoryClampNote.svelte";
  import AggregateReadout from "./AggregateReadout.svelte";
  import { TimeOfDayCursor } from "./time-of-day-cursor.svelte";
  import { atMinute, axisEnd } from "./daily-profile-view";
  import { loopAnalysisMark } from "./loop-analysis-tour.svelte";
  import {
    beganIn,
    dayInReviewSearch,
    distributionHour,
    hourOfSlot,
    longestFirst,
    shareBars,
    shareHour,
    SPARSE_BAND_FILL,
    type HourBar,
  } from "./loop-health-view";

  let { response }: { response: LoopHealthResponse } = $props();

  const primary = $derived(response.primary);
  const comparison = $derived(response.comparison ?? null);
  const comparing = $derived(comparison != null);

  const withheld = $derived(new Set(response.withheldSeries ?? []));
  const hidden = (series: LoopAnalysisSeries) => withheld.has(series);
  const hiddenText = () => "Hidden: your access does not include this data";

  /** Analysed time leaves every activity share measured, even at zero; none measured means none analysed. */
  const analysed = (range: LoopHealthRange | null | undefined) =>
    [range?.activity?.closedLoop, range?.activity?.pump, range?.activity?.cgm, range?.zeroDelivery?.algorithmZeroTemp].some(
      (share) => share?.percent != null
    ) ||
    range?.automaticBoluses?.perDay?.overall != null ||
    range?.sensitivityRatio?.overall != null;
  const hasData = $derived(analysed(primary));
  const everythingHidden = $derived(
    [
      LoopAnalysisSeries.LoopActivity,
      LoopAnalysisSeries.CgmActivity,
      LoopAnalysisSeries.ZeroDelivery,
      LoopAnalysisSeries.AutomaticBoluses,
      LoopAnalysisSeries.SensitivityRatio,
    ].every(hidden)
  );

  function reasonText(reason: LoopHealthUnknownReason | null | undefined): string {
    switch (reason) {
      case LoopHealthUnknownReason.NotRecordedByThisSystem:
        return "The system that ran the loop does not record this";
      case LoopHealthUnknownReason.NoCyclesInRange:
        return "No loop cycles in this range";
      case LoopHealthUnknownReason.NoReasonRecorded:
        return "No loop cycle in this range recorded the data that this figure needs";
      case LoopHealthUnknownReason.MaskedOut:
        return "All the data for this figure is in time that the filters exclude";
      case LoopHealthUnknownReason.SettingNotUploaded:
        return "The active profile does not record a maximum basal rate";
      case LoopHealthUnknownReason.NoAlgorithmTemps:
        return "The loop did not set a temp basal in this range";
      case LoopHealthUnknownReason.SetterNotFound:
        return "No loop cycle in this range set the temp basals that this figure needs";
      default:
        return "Nothing measured in this range";
    }
  }
  const unknown = (key: keyof LoopHealthUnknown) => reasonText(primary?.unknown?.[key]);

  const pct = (v: number) => `${v.toFixed(1)}%`;
  const maybePct = (v: number | null | undefined) => (v == null ? "–" : pct(v));
  const u1 = (v: number) => v.toFixed(1);
  const u2 = (v: number) => v.toFixed(2);
  const daysCounted = (n: number | null | undefined) => (n == null ? "–" : plural(n, ["# day", "# days"]));

  function duration(minutes: number | null | undefined): string {
    if (minutes == null) return "–";
    const m = Math.round(minutes);
    if (m < 60) return plural(m, ["# min", "# min"]);
    if (m >= 48 * 60) {
      const d = Math.floor(m / (24 * 60));
      const h = Math.round((m - d * 24 * 60) / 60);
      return h === 0 ? plural(d, ["# day", "# days"]) : `${plural(d, ["# day", "# days"])} ${plural(h, ["# h", "# h"])}`;
    }
    const h = Math.floor(m / 60);
    return m % 60 === 0 ? plural(h, ["# h", "# h"]) : `${plural(h, ["# h", "# h"])} ${plural(m % 60, ["# min", "# min"])}`;
  }

  /** A figure's note, with the comparison's value after it when there is one. */
  function noted(note: string | null, compared: string | null): string | undefined {
    const parts = [note, comparing ? `Compared: ${compared ?? "–"}` : null].filter((p): p is string => p != null);
    return parts.length > 0 ? parts.join(" · ") : undefined;
  }

  const hiddenFigure = (label: string): Figure => ({ label, value: "–", note: hiddenText() });
  const unknownFigure = (label: string, reason: string): Figure => ({ label, value: "Unknown", note: reason });

  function shareFigure(
    label: string,
    pick: (range: LoopHealthRange | null | undefined) => LoopHealthShare | null | undefined,
    note: string
  ): Figure {
    const value = pick(primary)?.percent;
    const compared = pick(comparison)?.percent;
    if (value == null) return { label, value: "–", note: noted("Nothing measured in this range", maybeComparedPct(compared)) };
    return { label, value: pct(value), note: noted(note, maybeComparedPct(compared)) };
  }
  const maybeComparedPct = (v: number | null | undefined) => (v == null ? null : pct(v));

  function gapsFigure(
    label: string,
    series: LoopAnalysisSeries,
    key: "loopGaps" | "unrecordedDelivery",
    none: string
  ): Figure {
    if (hidden(series)) return hiddenFigure(label);
    const gaps = primary?.cadence?.[key];
    if (gaps == null) return unknownFigure(label, unknown(key));
    const count = gaps.count ?? 0;
    const compared = comparison?.cadence?.[key];
    return {
      label,
      value: String(count),
      unit: plural(count, ["period", "periods"]),
      note: noted(
        count > 0 ? `${duration(gaps.totalMinutes)} in total, longest ${duration(gaps.longestMinutes)}` : none,
        compared == null
          ? null
          : `${plural(compared.count ?? 0, ["# period", "# periods"])}, ${duration(compared.totalMinutes)} in total`
      ),
    };
  }

  const figures = $derived.by<Figure[]>(() => {
    const list: Figure[] = [];

    list.push(
      hidden(LoopAnalysisSeries.LoopActivity)
        ? hiddenFigure("Closed loop")
        : shareFigure("Closed loop", (r) => r?.activity?.closedLoop, "Percent of the analysed time when the loop controlled basal delivery")
    );
    list.push(
      hidden(LoopAnalysisSeries.CgmActivity)
        ? hiddenFigure("CGM active")
        : shareFigure("CGM active", (r) => r?.activity?.cgm, "Percent of the analysed time when glucose readings arrived")
    );
    list.push(
      hidden(LoopAnalysisSeries.LoopActivity)
        ? hiddenFigure("Pump active")
        : shareFigure("Pump active", (r) => r?.activity?.pump, "Percent of the analysed time when the loop uploaded a cycle. If there are no cycles, the report uses the delivery records.")
    );
    list.push(
      gapsFigure("Loop gaps", LoopAnalysisSeries.LoopGaps, "loopGaps", `No gap of more than ${gapThreshold} between loop cycles`)
    );
    list.push(
      gapsFigure(
        "Unrecorded delivery",
        LoopAnalysisSeries.UnrecordedDelivery,
        "unrecordedDelivery",
        "The pump records show the delivery for all the time that the loop ran"
      )
    );

    if (hidden(LoopAnalysisSeries.ZeroDelivery)) list.push(hiddenFigure("Algorithm zero temps"));
    else {
      const zero = primary?.zeroDelivery;
      const value = zero?.algorithmZeroTemp?.percent;
      list.push({
        label: "Algorithm zero temps",
        value: maybePct(value),
        note: noted(
          value == null
            ? "The delivery is not known for this range"
            : `Percent of the time with known delivery when basal delivery was zero. Suspended ${maybePct(zero?.suspended?.percent)}, manual zero temps ${maybePct(zero?.manualZeroTemp?.percent)}`,
          maybeComparedPct(comparison?.zeroDelivery?.algorithmZeroTemp?.percent)
        ),
      });
    }

    if (hidden(LoopAnalysisSeries.MaxBasal)) list.push(hiddenFigure("At max basal"));
    else {
      const maxBasal = primary?.maxBasal;
      const value = maxBasal?.atCap?.percent;
      const known = maxBasal?.capKnown?.percent;
      if (value == null) list.push(unknownFigure("At max basal", unknown("maxBasal")));
      else
        list.push({
          label: "At max basal",
          value: pct(value),
          note: noted(
            `Percent of the algorithm temp basal time with a known max basal (${maybePct(known)})`,
            maybeComparedPct(comparison?.maxBasal?.atCap?.percent)
          ),
        });
    }

    if (hidden(LoopAnalysisSeries.AtMaxIob)) list.push(hiddenFigure("At max IOB"));
    else if (primary?.atMaxIob?.percent == null)
      list.push(unknownFigure("At max IOB", unknown("atMaxIob")));
    else
      list.push(
        shareFigure(
          "At max IOB",
          (r) => r?.atMaxIob,
          "Percent of the loop time with a recorded reason when max IOB limited the dose. Then the loop gave less insulin than it wanted. Insulin on board (IOB) was at or near max IOB."
        )
      );

    if (hidden(LoopAnalysisSeries.AutomaticBoluses)) list.push(hiddenFigure("Automatic boluses a day"));
    else {
      const perDay = primary?.automaticBoluses?.perDay?.overall?.mean;
      const size = primary?.automaticBoluses?.size?.overall;
      const compared = comparison?.automaticBoluses?.perDay?.overall?.mean;
      list.push({
        label: "Automatic boluses a day",
        value: perDay == null ? "–" : u1(perDay),
        note: noted(
          perDay == null
            ? "Nothing measured in this range"
            : size?.p50 == null
              ? "No automatic boluses in this range"
              : `Median size ${u2(size.p50)} U` +
                (size.p10 != null && size.p90 != null ? `, P10–P90 ${u2(size.p10)}–${u2(size.p90)} U` : ""),
          compared == null ? null : u1(compared)
        ),
      });
    }

    if (hidden(LoopAnalysisSeries.SuggestedNotEnacted)) list.push(hiddenFigure("Requested, not done"));
    else if (primary?.suggestedNotEnacted?.percent == null)
      list.push(unknownFigure("Requested, not done", unknown("suggestedNotEnacted")));
    else
      list.push(
        shareFigure(
          "Requested, not done",
          (r) => r?.suggestedNotEnacted,
          "Percent of the changes that the loop requested but that did not occur (open loop, or the pump did not respond)"
        )
      );

    if (hidden(LoopAnalysisSeries.LoopLimits)) list.push(hiddenFigure("Max basal rate setting of the Loop app"));
    else if (primary?.limits?.loopMaximumBasalRate == null)
      list.push(
        unknownFigure(
          "Max basal rate setting of the Loop app",
          primary?.unknown?.loopMaximumBasalRate === LoopHealthUnknownReason.NotRecordedByThisSystem
            ? "Only the Loop app uploads this setting"
            : unknown("loopMaximumBasalRate")
        )
      );
    else
      list.push({ label: "Max basal rate setting of the Loop app", value: u2(primary.limits.loopMaximumBasalRate), unit: "U/h" });

    if (hidden(LoopAnalysisSeries.OrefMaxIob)) list.push(hiddenFigure("Max IOB"));
    else if (primary?.limits?.orefMaxIob == null)
      list.push(
        unknownFigure(
          "Max IOB",
          primary?.unknown?.orefMaxIob === LoopHealthUnknownReason.NoReasonRecorded
            ? "This figure shows only when the loop reports that max IOB limited a dose. The loop did not report this in this range."
            : unknown("orefMaxIob")
        )
      );
    else list.push({ label: "Max IOB", value: u1(primary.limits.orefMaxIob), unit: "U" });

    return list;
  });

  const cursor = new TimeOfDayCursor();
  const gapThreshold = $derived(plural(response.loopGapMinutes ?? 0, ["# minute", "# minutes"]));
  const pickHelp = "Press Enter to show only the gaps that started in the hour. Press Escape to show all the gaps.";

  const activityStrips = $derived(
    [
      {
        id: "closed-loop",
        title: "Closed loop by hour",
        label: "Closed loop",
        series: LoopAnalysisSeries.LoopActivity,
        pick: (range: LoopHealthRange | null | undefined) => range?.activity?.closedLoop,
        none: "No loop activity in this range",
      },
      {
        id: "cgm-active",
        title: "CGM active by hour",
        label: "CGM active",
        series: LoopAnalysisSeries.CgmActivity,
        pick: (range: LoopHealthRange | null | undefined) => range?.activity?.cgm,
        none: "No glucose readings in this range",
      },
      {
        id: "pump-active",
        title: "Pump active by hour",
        label: "Pump active",
        series: LoopAnalysisSeries.LoopActivity,
        pick: (range: LoopHealthRange | null | undefined) => range?.activity?.pump,
        none: "No pump activity in this range",
      },
    ].map((strip) => ({
      ...strip,
      bars: shareBars(strip.pick(primary)),
      compared: shareBars(strip.pick(comparison)),
    }))
  );
  const zeroBars = $derived(shareBars(primary?.zeroDelivery?.algorithmZeroTemp));
  const zeroComparison = $derived(shareBars(comparison?.zeroDelivery?.algorithmZeroTemp));
  const capBars = $derived(shareBars(primary?.maxBasal?.atCap));
  const capComparison = $derived(shareBars(comparison?.maxBasal?.atCap));
  const sensitivityHours = $derived(primary?.sensitivityRatio?.hours ?? []);
  const sensitivityComparison = $derived(comparison?.sensitivityRatio?.hours ?? []);

  const percentEnd = (bars: HourBar[]) => Math.min(100, axisEnd(bars.map((b) => b.value), 5));
  const zeroDomain = $derived<[number, number]>([0, percentEnd([...zeroBars, ...zeroComparison])]);
  const capDomain = $derived<[number, number]>([0, percentEnd([...capBars, ...capComparison])]);
  const sensitivityDomain = $derived.by<[number, number]>(() => {
    const known = [...sensitivityHours, ...sensitivityComparison]
      .flatMap((h) => [h.value?.p25, h.value?.p75])
      .filter((v): v is number => v != null);
    const low = Math.min(1, ...known);
    const high = Math.max(1, ...known);
    const pad = Math.max(0.05, (high - low) * 0.1);
    return [Math.max(0, Math.floor((low - pad) * 10) / 10), Math.ceil((high + pad) * 10) / 10];
  });
  /** A tick in the bottom eighth of the plot would sit on the hour labels, so it goes unlabelled. */
  const sensitivityTick = (v: number) => {
    const [low, high] = sensitivityDomain;
    return v - low < (high - low) / 8 ? "" : v.toFixed(1);
  };

  const ACTIVITY = "var(--chart-2)";
  const ZERO = "var(--chart-3)";
  const CAP = "var(--chart-4)";
  const SENSITIVITY = "var(--chart-2)";

  function barKey(color: string, label: string, bars: HourBar[]): StripKeyItem[] {
    return [
      ...(comparing ? [{ mark: "bar" as const, label, color }, { mark: "compared" as const, label: "Compared range" }] : []),
      ...(bars.some((b) => b.sparse) ? [{ mark: "sparse" as const, label: "Too few days", color }] : []),
    ];
  }

  const emptyText = (series: LoopAnalysisSeries, available: boolean, none: string) =>
    hidden(series) ? hiddenText() : available ? null : none;

  const hourText = (hour: number) => `${hourLabel(atMinute(hour * 60))}–${hourLabel(atMinute(hour * 60 + 60))}`;

  function describeShare(share: LoopHealthShare | null | undefined, slot: number): string {
    const hour = shareHour(share, hourOfSlot(slot));
    if (hour?.percent == null) return "Nothing measured in this hour";
    return hour.daily?.n == null ? pct(hour.percent) : `${pct(hour.percent)}, ${daysCounted(hour.daily.n)}`;
  }

  function describeSensitivity(slot: number): string {
    const value = distributionHour(primary?.sensitivityRatio, hourOfSlot(slot))?.value;
    if (value?.p50 == null) return "Nothing measured in this hour";
    return `Median ${u2(value.p50)}, ${plural(value.n ?? 0, ["# loop cycle", "# loop cycles"])}`;
  }

  const pickedHour = $derived(cursor.selected == null ? null : hourOfSlot(cursor.selected));
  const crossesYear = $derived(
    (response.primaryRange?.startDate ?? "").slice(0, 4) !== (response.primaryRange?.endDate ?? "").slice(0, 4)
  );
  const gapDate = (gap: LoopHealthGap) => {
    const day = startOfDay(gap.date ?? "");
    return crossesYear ? formatMediumDate(day) : formatWeekdayDate(day);
  };

  const LIST_LENGTH = 10;
  let expanded = $state<Record<string, boolean>>({});

  const gapLists = $derived(
    [
      {
        key: "loop-gaps",
        title: "Loop gaps",
        explanation: `More than ${gapThreshold} between loop cycles.`,
        series: LoopAnalysisSeries.LoopGaps,
        reason: "loopGaps" as const,
        gaps: primary?.cadence?.loopGaps,
      },
      {
        key: "unrecorded-delivery",
        title: "Unrecorded delivery",
        explanation: "Time when the loop ran but the pump records do not show what the pump delivered.",
        series: LoopAnalysisSeries.UnrecordedDelivery,
        reason: "unrecordedDelivery" as const,
        gaps: primary?.cadence?.unrecordedDelivery,
      },
    ].map((list) => {
      const all = beganIn(longestFirst(list.gaps), pickedHour);
      return { ...list, all, shown: expanded[list.key] ? all : all.slice(0, LIST_LENGTH) };
    })
  );
  const pickedCount = $derived(gapLists.filter((l) => !hidden(l.series)).reduce((sum, l) => sum + l.all.length, 0));
</script>

{#snippet shareReadout(share: LoopHealthShare | null | undefined, compared: LoopHealthShare | null | undefined, slot: number)}
  {@const hour = shareHour(share, hourOfSlot(slot))}
  {@const other = shareHour(compared, hourOfSlot(slot))}
  <p class="text-muted-foreground">{hourText(hourOfSlot(slot))}</p>
  {#if hour?.percent == null}
    <p class="text-muted-foreground">Nothing measured in this hour</p>
  {:else}
    <dl class="grid grid-cols-[auto_auto] gap-x-3 tabular-nums">
      <dt class="text-muted-foreground">All days</dt>
      <dd class="text-right">{pct(hour.percent)}</dd>
      <dt class="text-muted-foreground">Median day</dt>
      <dd class="text-right">{maybePct(hour.daily?.p50)}</dd>
      <dt class="text-muted-foreground">Days counted</dt>
      <dd class="text-right">{daysCounted(hour.daily?.n)}</dd>
      {#if comparing}
        <dt class="text-muted-foreground">Compared, all days</dt>
        <dd class="text-right">{maybePct(other?.percent)}</dd>
      {/if}
    </dl>
    <p class="mt-1 text-muted-foreground">All days adds the time of every day in this hour. Median day is the share of the middle day.</p>
    {#if hour.daily?.sparse ?? true}
      <p class="text-muted-foreground">Too few days to show a pattern</p>
    {/if}
  {/if}
{/snippet}

{#if response.timeZoneUnavailableReason}
  <NoTimeZone reason={response.timeZoneUnavailableReason}>{loopAnalysisTimeZoneNeed()}</NoTimeZone>
{:else}
  <div class="space-y-6">
    <HistoryClampNote {response}>Each hour of the day uses data from one day at most.</HistoryClampNote>

    <IgnoredFiltersNotice ignored={response.ignoredFilters} />

    {#if !hasData}
      <div class="rounded-lg border border-dashed p-8 text-center text-muted-foreground">
        {#if everythingHidden}
          <p class="font-medium">Your access does not include the data for this page</p>
        {:else}
          <p class="font-medium">No data in this range</p>
          <p class="text-sm">Select a longer range, or use fewer filters.</p>
        {/if}
      </div>
    {:else}
      <div {@attach loopAnalysisMark("loop-analysis-loop-health.tiles")}>
        <FigureStrip {figures} />
      </div>

      <div
        class="space-y-5 rounded-lg border bg-card p-3 @md:p-4"
        data-testid="loop-analysis-health-strips"
        {@attach loopAnalysisMark("loop-analysis-loop-health.hours")}
      >
        {#each activityStrips as strip (strip.id)}
          <div class="space-y-1">
            <TimeOfDayPanel
              slotHeading={false}
              id={strip.id}
              title={strip.title}
              unit="Share of analysed time, %"
              {cursor}
              {pickHelp}
              yDomain={[0, 100]}
              heightClass="h-24"
              describe={(slot) => describeShare(strip.pick(primary), slot)}
              empty={emptyText(strip.series, strip.bars.length > 0, strip.none)}
            >
              {#snippet marks()}
                <HourBars bars={strip.bars} comparison={strip.compared} color={ACTIVITY} />
              {/snippet}
              {#snippet tooltip(slot)}
                {@render shareReadout(strip.pick(primary), strip.pick(comparison), slot)}
              {/snippet}
            </TimeOfDayPanel>
            {#if !hidden(strip.series)}
              <StripKey items={barKey(ACTIVITY, strip.label, strip.bars)} />
            {/if}
          </div>
        {/each}

        <div class="space-y-1">
          <TimeOfDayPanel
            slotHeading={false}
            id="zero-delivery"
            title="Algorithm zero temps by hour"
            unit="Share of time with known delivery, %"
            {cursor}
            {pickHelp}
            yDomain={zeroDomain}
            heightClass="h-28"
            describe={(slot) => describeShare(primary?.zeroDelivery?.algorithmZeroTemp, slot)}
            empty={emptyText(LoopAnalysisSeries.ZeroDelivery, zeroBars.length > 0, "The delivery is not known for this range")}
          >
            {#snippet marks()}
              <HourBars bars={zeroBars} comparison={zeroComparison} color={ZERO} />
            {/snippet}
            {#snippet tooltip(slot)}
              {@const hour = hourOfSlot(slot)}
              {@const suspended = shareHour(primary?.zeroDelivery?.suspended, hour)?.percent}
              {@const manual = shareHour(primary?.zeroDelivery?.manualZeroTemp, hour)?.percent}
              {@render shareReadout(primary?.zeroDelivery?.algorithmZeroTemp, comparison?.zeroDelivery?.algorithmZeroTemp, slot)}
              <p class="mt-1 tabular-nums">
                <span class="text-muted-foreground">Suspended</span> {maybePct(suspended)}
                <span class="ml-2 text-muted-foreground">Manual zero temps</span> {maybePct(manual)}
              </p>
            {/snippet}
          </TimeOfDayPanel>
          {#if !hidden(LoopAnalysisSeries.ZeroDelivery)}
            <StripKey items={barKey(ZERO, "Algorithm zero temps", zeroBars)} />
          {/if}
        </div>

        <div class="space-y-1">
          <TimeOfDayPanel
            slotHeading={false}
            id="max-basal"
            title="At max basal by hour"
            unit="Share of algorithm temp basal time with a known max basal, %"
            {cursor}
            {pickHelp}
            yDomain={capDomain}
            heightClass="h-28"
            describe={(slot) => describeShare(primary?.maxBasal?.atCap, slot)}
            empty={emptyText(LoopAnalysisSeries.MaxBasal, capBars.length > 0, `Unknown: ${unknown("maxBasal")}`)}
          >
            {#snippet marks()}
              <HourBars bars={capBars} comparison={capComparison} color={CAP} />
            {/snippet}
            {#snippet tooltip(slot)}
              {@const known = shareHour(primary?.maxBasal?.capKnown, hourOfSlot(slot))?.percent}
              {@render shareReadout(primary?.maxBasal?.atCap, comparison?.maxBasal?.atCap, slot)}
              <p class="mt-1 tabular-nums">
                <span class="text-muted-foreground">Max basal known for</span> {maybePct(known)}
              </p>
            {/snippet}
          </TimeOfDayPanel>
          {#if !hidden(LoopAnalysisSeries.MaxBasal)}
            <StripKey items={barKey(CAP, "At max basal", capBars)} />
          {/if}
        </div>

        <div class="space-y-1">
          <TimeOfDayPanel
            slotHeading={false}
            id="sensitivity"
            title="Sensitivity ratio by hour"
            unit="Autosens or dynamic ISF. Above 1 means that the loop calculated a need for more insulin."
            {cursor}
            {pickHelp}
            yDomain={sensitivityDomain}
            yFormat={sensitivityTick}
            heightClass="h-32"
            showTimeAxis
            describe={describeSensitivity}
            empty={emptyText(
              LoopAnalysisSeries.SensitivityRatio,
              sensitivityHours.some((h) => h.value?.p50 != null),
              "The loop did not report a sensitivity ratio in this range"
            )}
          >
            {#snippet marks()}
              <Rule y={1} class="stroke-foreground/40" />
              <HourBands hours={sensitivityHours} comparison={sensitivityComparison} color={SENSITIVITY} />
            {/snippet}
            {#snippet tooltip(slot)}
              {@const hour = hourOfSlot(slot)}
              <p class="text-muted-foreground">{hourText(hour)}</p>
              <AggregateReadout
                value={distributionHour(primary?.sensitivityRatio, hour)?.value}
                comparison={distributionHour(comparison?.sensitivityRatio, hour)?.value}
                showComparison={comparing}
                format={u2}
                counts="cycles"
              />
            {/snippet}
          </TimeOfDayPanel>
          {#if !hidden(LoopAnalysisSeries.SensitivityRatio)}
            <StripKey
              items={[
                { mark: "band", label: "25–75%", color: SENSITIVITY },
                { mark: "line", label: "Median", color: SENSITIVITY },
                { mark: "reference", label: "1.0 = usual sensitivity" },
                ...(comparing ? [{ mark: "compared" as const, label: "Compared range" }] : []),
                ...(sensitivityHours.some((h) => h.value != null && (h.value.sparse ?? true))
                  ? [{ mark: "sparse" as const, label: "Too few days", color: SENSITIVITY, fill: SPARSE_BAND_FILL }]
                  : []),
              ]}
            />
          {/if}
        </div>

        <p class="flex items-center gap-1.5 px-1 text-xs text-muted-foreground">
          <EyeOff class="size-3.5" />
          Faded hours have too few days to show a pattern. To list only the gaps that started in an
          hour, select the hour or press Enter on it. Use the arrow keys to move along the day and
          between charts.
        </p>
      </div>

      <section class="space-y-4 rounded-lg border bg-card p-4" aria-labelledby="loop-health-gaps" {@attach loopAnalysisMark("loop-analysis-loop-health.gaps")}>
        <div class="flex flex-wrap items-baseline justify-between gap-2">
          <h3 id="loop-health-gaps" class="font-medium">Gaps, longest first</h3>
          {#if pickedHour != null}
            <Button variant="link" size="inline" class="print:hidden" onclick={() => (cursor.selected = null)}>Show all hours</Button>
          {/if}
        </div>
        {#if pickedHour != null}
          <p class="text-sm text-muted-foreground">Only the gaps that started in {hourText(pickedHour)}.</p>
        {/if}
        <p class="sr-only" aria-live="polite">
          {#if pickedHour != null}{plural(pickedCount, ["# gap started in this hour", "# gaps started in this hour"])}{/if}
        </p>

        {#each gapLists as list (list.key)}
          <div class="space-y-1.5">
            <h4 class="text-sm font-medium">{list.title}</h4>
            {#if hidden(list.series)}
              <p class="text-sm text-muted-foreground">{hiddenText()}</p>
            {:else if list.gaps == null}
              <p class="text-sm text-muted-foreground">Unknown: {unknown(list.reason)}</p>
            {:else}
              <p class="text-sm text-muted-foreground">{list.explanation}</p>
              {#if list.all.length === 0}
                <p class="text-sm text-muted-foreground">None</p>
              {:else}
                <ul id={`${list.key}-list`} class="divide-y rounded-md border">
                  {#each list.shown as gap, i (`${gap.start}${i}`)}
                    <li>
                      <a
                        href={resolve(`/reports/day-in-review?${dayInReviewSearch(gap)}`)}
                        class="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 text-sm hover:bg-muted/50"
                      >
                        <span class="min-w-32 font-medium">{gapDate(gap)}</span><span class="sr-only">, </span>
                        <span class="min-w-16 tabular-nums">{time(atMinute(gap.startMinuteOfDay ?? 0))}</span><span class="sr-only">, </span>
                        <span class="flex-1 tabular-nums">{duration(gap.minutes)}</span>
                        <span class="sr-only">. Open in Day in Review</span>
                        <ArrowUpRight class="size-4 text-muted-foreground" aria-hidden="true" />
                      </a>
                    </li>
                  {/each}
                </ul>
                {#if list.all.length > LIST_LENGTH}
                  <Button
                    variant="link"
                    size="inline"
                    class="print:hidden"
                    aria-expanded={expanded[list.key] ?? false}
                    aria-controls={`${list.key}-list`}
                    onclick={() => (expanded = { ...expanded, [list.key]: !expanded[list.key] })}
                  >
                    {expanded[list.key]
                      ? plural(LIST_LENGTH, ["Show the longest #", "Show the longest #"])
                      : plural(list.all.length, ["Show all #", "Show all #"])}
                  </Button>
                {/if}
              {/if}
            {/if}
          </div>
        {/each}
      </section>
    {/if}
  </div>
{/if}
