<!--
  The Meals and events page body: each cohort's events aligned on their anchors, every series on one shared
  axis of minutes from the event, stacked so a single crosshair reads them all; then each cohort's
  figures side by side and the events themselves. Values come from the API as they are.
-->
<script lang="ts">
  import { Rule } from "layerchart";
  import {
    LoopAnalysisSeries,
    type EventCohortResult,
    type EventResponseReport,
    type EventResponseSlot,
    type LoopAnalysisAggregate,
  } from "#lib/api/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { plural } from "#lib/utils/plural.js";
  import { tenantChartThresholds } from "#lib/stores/realtime-store.svelte.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { bg, bgDelta, bgLabel, convertToDisplayUnits } from "#lib/utils/formatting.js";
  import Info from "@lucide/svelte/icons/info";
  import TimeOfDayPanel from "./TimeOfDayPanel.svelte";
  import PercentileBands from "./PercentileBands.svelte";
  import AggregateReadout from "./AggregateReadout.svelte";
  import EventMetricsTable from "./EventMetricsTable.svelte";
  import EventList from "./EventList.svelte";
  import EventSlotEvents from "./EventSlotEvents.svelte";
  import NoTimeZone from "#lib/components/reports/NoTimeZone.svelte";
  import { loopAnalysisTimeZoneNeed } from "./time-zone-need.svelte";
  import CohortDot from "./CohortDot.svelte";
  import { TimeOfDayCursor } from "./time-of-day-cursor.svelte";
  import { axisEnd, axisStart, signedTwoPlaces as signedUnits } from "./daily-profile-view";
  import { cohortBands, hourTicks, slotMinute } from "./event-response-view";
  import { cohortLabel, relativeHours, relativeMinutes } from "./event-labels.svelte";
  import IgnoredFiltersNotice from "./IgnoredFiltersNotice.svelte";
  import { eventOptionLabels } from "./filter-labels.svelte";
  import HistoryClampNote from "./HistoryClampNote.svelte";
  import { loopAnalysisMark } from "./loop-analysis-tour.svelte";

  interface Props {
    response: EventResponseReport;
    /** Values the page removed from the URL after an earlier response ignored them, which this response no longer names. */
    cleanedFilters?: string[];
  }

  let { response, cleanedFilters = [] }: Props = $props();

  const cohorts = $derived(response.definition?.cohorts ?? []);
  const primary = $derived(response.primary?.cohorts ?? []);
  const comparison = $derived(response.comparison?.cohorts ?? null);
  const comparing = $derived(comparison != null);
  const minutes = $derived(response.slotMinutes ?? []);
  const anyEvents = $derived([...primary, ...(comparison ?? [])].some((c) => (c.events ?? 0) > 0));

  // The axis is fixed per response; a cursor per slot count keeps the stack's tab stop and crosshair.
  const cursor = $derived(new TimeOfDayCursor(Math.max(1, minutes.length)));
  let algorithmOnly = $state(false);
  const options = $derived(eventOptionLabels());

  const units = $derived(glucoseUnits.current);
  const toDisplay = $derived((mgdl: number) => convertToDisplayUnits(mgdl, units));
  const thresholds = tenantChartThresholds();

  const withheld = $derived(new Set(response.withheldSeries ?? []));
  const hidden = (series: LoopAnalysisSeries) => withheld.has(series);
  const hiddenText = () => "Hidden: your access does not include this data";

  const resultFor = (list: EventCohortResult[] | null, cohort: number | undefined) =>
    list?.find((r) => r.cohort === cohort);

  /** The events a cohort left out, by why. */
  function droppedText(result: EventCohortResult | undefined): string {
    const n = (count: number | undefined) => count ?? 0;
    return [
      n(result?.notIsolated) > 0
        ? plural(n(result?.notIsolated), ["# not included because of another event in its window", "# not included because of another event in their window"])
        : null,
      n(result?.missingAnchorGlucose) > 0
        ? plural(n(result?.missingAnchorGlucose), ["# without a glucose reading at the time", "# without a glucose reading at the time"])
        : null,
      n(result?.beforeVisibleHistory) > 0
        ? plural(n(result?.beforeVisibleHistory), [
            "# not included because the time before it is outside the history that you can see",
            "# not included because the time before them is outside the history that you can see",
          ])
        : null,
    ]
      .filter((text) => text !== null)
      .join("; ");
  }

  interface Panel {
    id: string;
    title: string;
    unit: string;
    series: LoopAnalysisSeries;
    pick: (slot: EventResponseSlot) => LoopAnalysisAggregate | undefined;
    format: (value: number) => string;
    scale?: (value: number) => number;
    domain: [number, number];
    zero?: boolean;
    yFormat?: (value: number) => string;
  }

  const allSlots = $derived([...primary, ...(comparison ?? [])].flatMap((c) => c.slots ?? []));
  const spread = (pick: Panel["pick"]) => allSlots.flatMap((s) => [pick(s)?.p10, pick(s)?.p90]);
  const signedDomain = (values: (number | null | undefined)[], floor: number): [number, number] => [
    -axisEnd(values.map((v) => (v == null ? null : -v)), floor),
    axisEnd(values, floor),
  ];

  const glucoseChange = (s: EventResponseSlot) => s.glucoseChange;
  const cumulative = $derived((s: EventResponseSlot) =>
    algorithmOnly ? s.netLoopCorrectionSinceAnchor : s.netInsulinSinceAnchor
  );
  const u2 = (v: number) => v.toFixed(2);
  /** Whole numbers in mg/dL; in mmol/L, as many decimals as the axis span needs so ticks stay distinct. */
  const axisGlucose = (domain: [number, number]) => (v: number) =>
    units === "mmol" ? v.toFixed(domain[1] - domain[0] < 6 ? 1 : 0) : String(Math.round(v));

  const changeDomain = $derived(
    signedDomain(spread(glucoseChange).map((v) => (v == null ? null : toDisplay(v))), units === "mmol" ? 1 : 20)
  );
  const glucoseDomain = $derived<[number, number]>([0, toDisplay(thresholds.current.glucoseYMax)]);

  const panels = $derived<Panel[]>([
    {
      id: "glucose-change",
      title: "Glucose change from the event",
      unit: bgLabel(),
      series: LoopAnalysisSeries.EventGlucose,
      pick: glucoseChange,
      format: (v) => bgDelta(v),
      scale: toDisplay,
      domain: changeDomain,
      zero: true,
      yFormat: axisGlucose(changeDomain),
    },
    {
      id: "glucose",
      title: "Glucose",
      unit: bgLabel(),
      series: LoopAnalysisSeries.EventGlucose,
      pick: (s) => s.glucose,
      format: (v) => String(bg(v)),
      scale: toDisplay,
      domain: glucoseDomain,
      yFormat: axisGlucose(glucoseDomain),
    },
    {
      id: "correction",
      title: "Net loop correction",
      unit: "U/h above schedule",
      series: LoopAnalysisSeries.EventNetInsulin,
      pick: (s) => s.netLoopCorrection,
      format: signedUnits,
      domain: signedDomain(spread((s) => s.netLoopCorrection), 0.25),
      zero: true,
    },
    {
      id: "cumulative",
      title: algorithmOnly ? "Net loop correction since the event" : "Net insulin above schedule since the event",
      unit: "U",
      series: LoopAnalysisSeries.EventNetInsulin,
      pick: cumulative,
      format: signedUnits,
      domain: signedDomain(spread(cumulative), 0.5),
      zero: true,
    },
    {
      id: "iob",
      title: "Insulin on board",
      unit: "U",
      series: LoopAnalysisSeries.EventIob,
      pick: (s) => s.iob,
      format: u2,
      domain: [axisStart(spread((s) => s.iob)), axisEnd(spread((s) => s.iob), 1)],
    },
    {
      id: "cob",
      title: "Carbs on board",
      unit: "g",
      series: LoopAnalysisSeries.EventCob,
      pick: (s) => s.cob,
      format: (v) => v.toFixed(1),
      domain: [0, axisEnd(spread((s) => s.cob), 10)],
    },
  ]);

  const ticks = $derived(hourTicks(minutes));
  const slotLabel = (slot: number) =>
    `${relativeMinutes(slotMinute(minutes, slot))} to ${relativeMinutes(slotMinute(minutes, slot + 1))}`;
  const zeroSlot = $derived(Math.max(0, minutes.indexOf(0)));
  const slotOf = (list: EventCohortResult[] | null, cohort: number | undefined, slot: number) =>
    resultFor(list, cohort)?.slots?.[slot] ?? {};

  function describe(panel: Panel, slot: number): string {
    return cohorts
      .map((cohort) => {
        const value = panel.pick(slotOf(primary, cohort.cohort, slot));
        const label = cohortLabel(cohort);
        return value
          ? `${label}: median ${panel.format(value.p50 ?? 0)}, ${plural(value.n ?? 0, ["# event", "# events"])}`
          : `${label}: no data`;
      })
      .join("; ");
  }

  const emptyText = (panel: Panel) =>
    hidden(panel.series)
      ? hiddenText()
      : allSlots.some((s) => panel.pick(s) != null)
        ? null
        : "No data recorded near these events";

  const listed = $derived(!hidden(LoopAnalysisSeries.EventList));

  function closeEvents() {
    cursor.selected = null;
    cursor.restoreFocus();
  }
</script>

{#if response.timeZoneUnavailableReason}
  <NoTimeZone reason={response.timeZoneUnavailableReason}>{loopAnalysisTimeZoneNeed()}</NoTimeZone>
{:else}
  <div class="space-y-6" data-testid="loop-analysis-event-response">
    <HistoryClampNote {response} />

    <IgnoredFiltersNotice ignored={[...new Set([...(response.ignoredFilters ?? []), ...cleanedFilters])]} />

    {#if cohorts.length === 0}
      <div class="rounded-lg border border-dashed p-8 text-center text-muted-foreground">
        <p class="font-medium">No events to align</p>
        <p class="text-sm">Select an event that your access includes.</p>
      </div>
    {:else}
      <ul class="space-y-1.5 text-sm" aria-label="Cohorts">
        {#each cohorts as cohort, i (cohort.cohort)}
          {@const result = resultFor(primary, cohort.cohort)}
          {@const compared = resultFor(comparison, cohort.cohort)}
          <li class="flex items-start gap-2">
            <CohortDot index={i} class="mt-1.5" />
            <span>
              <span class="font-medium">{cohortLabel(cohort)}</span>:
              {plural(result?.events ?? 0, ["# event", "# events"])}
              {#if comparing}<span class="text-muted-foreground">(compared {compared?.events ?? 0})</span>{/if}
              {#if droppedText(result)}
                <span class="block text-xs text-muted-foreground">{droppedText(result)}</span>
              {/if}
            </span>
          </li>
        {/each}
      </ul>
      <p class="text-xs text-muted-foreground" data-testid="loop-analysis-event-window">
        {plural(response.definition?.minutesBefore ?? 0, ["# minute before", "# minutes before"])} to
        {plural((response.definition?.minutesAfter ?? 0) / 60, ["# hour after", "# hours after"])} each event.
        {#if response.definition?.isolatedOnly}{options.isolatedOnly}.{/if}
        {#if response.definition?.requireCorroboration}{options.requireCorroboration}.{/if}
      </p>

      {#if !anyEvents}
        <div class="rounded-lg border border-dashed p-8 text-center text-muted-foreground">
          <p class="font-medium">No events like these in this range</p>
          <p class="text-sm">Select a longer range, make the cohorts wider, or use fewer filters.</p>
        </div>
      {:else}
        <div class="space-y-5 rounded-lg border bg-card p-3 @md:p-4" data-testid="loop-analysis-event-curves" {@attach loopAnalysisMark("loop-analysis-meals-and-events.curves")}>
          {#if comparing}
            <p class="text-xs text-muted-foreground">
              The charts show the comparison range as dashed outlines in the color of each cohort.
            </p>
          {/if}

          {#each panels as panel, p (panel.id)}
            <TimeOfDayPanel
              id={panel.id}
              title={panel.title}
              unit={panel.unit}
              {cursor}
              yDomain={panel.domain}
              yFormat={panel.yFormat}
              heightClass={p < 2 ? "h-44" : "h-32"}
              showTimeAxis={p === panels.length - 1}
              {ticks}
              tickLabel={(slot) => relativeHours(slotMinute(minutes, slot))}
              {slotLabel}
              pickHelp={listed ? "Press Enter to list the events for the time. Press Escape to close the list." : null}
              empty={emptyText(panel)}
              describe={(slot) => describe(panel, slot)}
            >
              {#snippet marks()}
                <Rule x={zeroSlot} class="stroke-foreground/50" stroke-dasharray="2 2" />
                {#if panel.zero}<Rule y={0} class="stroke-foreground/40" />{/if}
                {#each cohorts as cohort, i (cohort.cohort)}
                  {@const bands = cohortBands(i)}
                  <PercentileBands
                    slots={resultFor(primary, cohort.cohort)?.slots ?? []}
                    comparison={resultFor(comparison, cohort.cohort)?.slots ?? null}
                    pick={panel.pick}
                    scale={panel.scale}
                    outer={cohorts.length === 1}
                    outerColor={bands.outer}
                    innerColor={bands.inner}
                    lineColor={bands.median}
                    comparisonColor={bands.median}
                  />
                {/each}
              {/snippet}
              {#snippet tooltip(slot)}
                <div class="space-y-2">
                  {#each cohorts as cohort (cohort.cohort)}
                    <AggregateReadout
                      label={cohortLabel(cohort)}
                      value={panel.pick(slotOf(primary, cohort.cohort, slot))}
                      comparison={panel.pick(slotOf(comparison, cohort.cohort, slot))}
                      showComparison={comparing}
                      format={panel.format}
                      counts="events"
                    />
                  {/each}
                </div>
              {/snippet}
            </TimeOfDayPanel>
            {#if panel.id === "cumulative" && !hidden(panel.series)}
              <div class="flex items-center gap-2 px-1 print:hidden">
                <Switch id="event-algorithm-only" bind:checked={algorithmOnly} />
                <Label for="event-algorithm-only">Net loop correction only</Label>
              </div>
            {/if}
          {/each}

          <p class="flex items-center gap-1.5 px-1 text-xs text-muted-foreground">
            <Info class="size-3.5" />
            Each band shows the events of a cohort, aligned on the event at 0. Faded areas have too few
            events to show a pattern. {#if listed}To list the events with a glucose reading at a time,
              select the time or press Enter on it.{/if} Use the arrow keys to move along the window and
            between charts. Use Home and End to go to the start and the end.
          </p>
        </div>

        {#if listed && cursor.selected != null}
          <EventSlotEvents
            slot={cursor.selected}
            label={slotLabel(cursor.selected)}
            events={response.primary?.events ?? []}
            {cohorts}
            focusOnPick={cursor.selectedByKeyboard}
            onclose={closeEvents}
          />
        {/if}

        <section
          class="space-y-2"
          aria-labelledby="event-metrics-heading"
          data-testid="loop-analysis-event-metrics"
          {@attach loopAnalysisMark("loop-analysis-meals-and-events.metrics")}
        >
          <h3 id="event-metrics-heading" class="text-sm font-medium">Results after each event</h3>
          <EventMetricsTable {cohorts} {primary} {comparison} backToStartMgdl={response.backToStartMgdl} />
        </section>

        <section class="space-y-2" aria-labelledby="event-list-heading" {@attach listed ? loopAnalysisMark("loop-analysis-meals-and-events.events") : undefined}>
          <h3 id="event-list-heading" class="text-sm font-medium">Events</h3>
          {#if !listed}
            <p class="text-sm text-muted-foreground">{hiddenText()}</p>
          {:else}
            <EventList events={response.primary?.events ?? []} {cohorts} backToStartMgdl={response.backToStartMgdl} />
          {/if}
        </section>
      {/if}
    {/if}
  </div>
{/if}
