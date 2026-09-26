<script lang="ts">
  import { indexBy } from "$lib/utils/collections";
  import {
    Card,
    CardAction,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import * as Select from "$lib/components/ui/select";
  import { Moon, CalendarRange } from "lucide-svelte";
  import {
    Actogram,
    buildDayRange,
    type ActogramRowContext,
  } from "$lib/components/actogram";
  import { MS_PER_HOUR, HOURS_PER_ROW, type ActogramPoint } from "$lib/components/actogram/actogram";
  import { getTrends } from "$api/generated/sleepReports.generated.remote";
  import { useActogramReport } from "$lib/hooks/actogram-report.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { resolve } from "$app/paths";
  import { dayKeyFor, buildNightsByDayKey } from "$lib/utils/sleep-night-mapping";
  import { bgDelta, bgLabel, formatShortDate } from "$lib/utils/formatting";
  import FigureStrip, { type Figure } from "$lib/components/reports/FigureStrip.svelte";
  import SleepCompositionChart from "$lib/components/reports/sleep/SleepCompositionChart.svelte";
  import SleepWeeklyBreakdown from "$lib/components/reports/sleep/SleepWeeklyBreakdown.svelte";
  import { SleepSource } from "$api";
  import { laneForStage, laneTexture } from "$lib/utils/sleep-stages";
  import { patternClass } from "$lib/components/charts/print/chart-print-patterns";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";
  import { Artwork } from "@nocturne/watercolour";

  const VISIBLE_DAYS = 14;

  const report = useActogramReport("Error Loading Sleep Report");
  const { params: reportsParams, resource: actogramResource } = report;

  /**
   * "All sources" is a frontend-only sentinel; omitted from the request when
   * selected. Held in the URL so a filtered report can be refreshed and shared.
   */
  const viewParams = useSearchParams(
    z.object({ source: z.enum(SleepSource).nullable().default(null) }),
    { showDefaults: false, noScroll: true }
  );
  const sourceFilter = $derived<SleepSource | "all">(viewParams.source ?? "all");

  const sourceOptions: { value: SleepSource | "all"; label: string }[] = [
    { value: "all", label: "All sources" },
    { value: SleepSource.Apple, label: "Apple" },
    { value: SleepSource.Google, label: "Google" },
    { value: SleepSource.Fitbit, label: "Fitbit" },
    { value: SleepSource.Oura, label: "Oura" },
    { value: SleepSource.Garmin, label: "Garmin" },
    { value: SleepSource.Samsung, label: "Samsung" },
    { value: SleepSource.Manual, label: "Manual" },
  ];

  const sourceLabel = $derived(
    sourceOptions.find((o) => o.value === sourceFilter)?.label ?? "All sources"
  );

  // Summary tiles + composition chart come from the backend trends report,
  // fetched over the user-selected (unpadded) range so tiles/coverage/chart
  // reflect exactly the picker range — unlike the actogram, which loads the
  // padded window for double-plot context.
  const trendsResource = contextResource(
    () =>
      getTrends({
        from: new Date(reportsParams.dateRangeMillis.from).toISOString(),
        to: new Date(reportsParams.dateRangeMillis.to).toISOString(),
        source: sourceFilter === "all" ? undefined : sourceFilter,
      }),
    { errorTitle: "Error Loading Sleep Report" }
  );

  // Unpadded, user-selected day range for the composition chart — includes
  // gap days with no recorded night so tracking gaps stay visible.
  const selectedRangeDays = $derived(
    buildDayRange(reportsParams.dateRangeMillis.from, reportsParams.dateRangeMillis.to)
  );

  // Convert sleep spans into ActogramPoints (use midpoint of each span)
  // Each point carries startMills and endMills for rectangle rendering
  const sleepPoints = $derived(
    (actogramResource.current?.sleepSpans ?? []).map((s) => ({
      mills: s.startMills,
      startMills: s.startMills,
      endMills: s.endMills,
      state: s.state,
    }))
  );

  function sleepSpanOf(point: ActogramPoint) {
    const { startMills, endMills, state } = point;
    return {
      startMills: typeof startMills === "number" ? startMills : point.mills,
      endMills: typeof endMills === "number" ? endMills : point.mills,
      state: typeof state === "string" ? state : "",
    };
  }

  const actogramLegend = $derived.by(() => {
    const lanes = new Set(sleepPoints.map((p) => laneForStage(String(p.state ?? ""))));
    return [
      { lane: "deep", label: "Deep" },
      { lane: "rem", label: "REM" },
      { lane: "light", label: "Light" },
      { lane: "awake", label: "Awake" },
      { lane: "unspecified", label: "Asleep (unstaged)" },
    ]
      .filter((s) => lanes.has(s.lane))
      .map((s) => ({ texture: laneTexture(s.lane), label: s.label }));
  });

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  const sleepSummary = $derived(trendsResource.current?.summary);
  const sleepNights = $derived(trendsResource.current?.nights ?? []);
  const sleepWeeks = $derived(trendsResource.current?.weeks ?? []);

  // Maps each display day to the night's authoritative display date, for actogram row links.
  const nightDateByDayKey = $derived(
    indexBy(
      buildNightsByDayKey(sleepNights),
      ([key, night]) => (night.displayDate ? key : null),
      ([, night]) => night.displayDate ?? ""
    )
  );

  function formatHoursMinutes(hours: number): string {
    const totalMinutes = Math.round(hours * 60);
    return `${Math.floor(totalMinutes / 60)}h ${totalMinutes % 60}m`;
  }

  function formatRowLabelDate(day: Date): string {
    return formatShortDate(day);
  }

  const hasActogramSleep = $derived(sleepPoints.length > 0);
  const hasTrendsNights = $derived((sleepSummary?.nightCount ?? 0) > 0);
  const fullyEmpty = $derived(!hasTrendsNights && !hasActogramSleep);
  const showCompositionCard = $derived(hasTrendsNights);

  function signedNumber(value: number, digits = 0): string {
    const abs = Math.abs(value);
    if (abs < (digits === 0 ? 0.5 : 0.05)) return "±0";
    const sign = value > 0 ? "+" : "−";
    return `${sign}${abs.toFixed(digits)}`;
  }

  function withDelta(caption: string, delta: string | null): string {
    return delta == null ? caption : `${caption} · ${delta} vs prior 7 nights`;
  }

  const priorWeek = $derived(sleepSummary?.last7dVsPrior7d);
  const scoreDelta = $derived(
    priorWeek?.scoreDelta == null ? null : signedNumber(priorWeek.scoreDelta)
  );
  const tirDelta = $derived(
    priorWeek?.tirDelta == null ? null : `${signedNumber(priorWeek.tirDelta)} pp`
  );
  const dawnDelta = $derived(
    priorWeek?.dawnRiseDelta == null ? null : `${bgDelta(priorWeek.dawnRiseDelta)} ${bgLabel()}`
  );

  const scoredNightsCount = $derived(sleepNights.filter((n) => n.sleepScore != null).length);
  const hasComputedScore = $derived(
    sleepNights.some((n) => n.sleepScore != null && n.scoreSource === "Computed")
  );
  const scoreCaption = $derived.by(() => {
    const base = `avg of ${scoredNightsCount} scored night${scoredNightsCount === 1 ? "" : "s"}`;
    return hasComputedScore ? `${base} · includes estimated` : base;
  });

  const tirNightsCount = $derived(sleepNights.filter((n) => n.overnightTirPct != null).length);
  const tirCaption = $derived(
    `${tirNightsCount} night${tirNightsCount === 1 ? "" : "s"} with CGM`
  );

  const nightsTrackedCaption = $derived(
    `${Math.round(sleepSummary?.coveragePct ?? 0)}% of nights`
  );
  const lowsCaption = $derived(
    `${Math.round(sleepSummary?.nightsWithHypoPct ?? 0)}% of nights`
  );

  const sleepFigures = $derived.by((): Figure[] => {
    const s = sleepSummary;
    const figures: Figure[] = [];
    if (hasTrendsNights) {
      figures.push({
        label: "Average sleep",
        value: formatHoursMinutes((s?.meanAsleepMinutes ?? 0) / 60),
        note: "per night",
      });
    }
    figures.push({
      label: "Nights tracked",
      value: `${s?.nightCount ?? 0} of ${s?.daysInRange ?? 0}`,
      unit: "nights",
      note: nightsTrackedCaption,
    });
    if (s?.meanScore != null) {
      figures.push({
        label: "Sleep score",
        value: Math.round(s.meanScore).toString(),
        note: withDelta(scoreCaption, scoreDelta),
      });
    }
    if (s?.meanHrvMs != null) {
      figures.push({
        label: "HRV",
        value: Math.round(s.meanHrvMs).toString(),
        unit: "ms",
        note: "overnight average",
      });
    }
    return figures;
  });

  // meanTirPct is non-null exactly when overnight CGM data exists; hypo counts are only
  // meaningful on CGM nights, so the lows figure shares the TIR figure's gate.
  const overnightFigures = $derived.by((): Figure[] => {
    const s = sleepSummary;
    const figures: Figure[] = [];
    if (s?.meanTirPct != null) {
      figures.push({
        label: "Overnight TIR",
        value: Math.round(s.meanTirPct).toString(),
        unit: "%",
        note: withDelta(tirCaption, tirDelta),
      });
    }
    if (s?.meanDawnRiseMg != null) {
      figures.push({
        label: "Dawn rise",
        value: bgDelta(s.meanDawnRiseMg, true),
        unit: bgLabel(),
        note: withDelta("avg pre-wake change", dawnDelta),
      });
    }
    if (s?.meanTirPct != null) {
      figures.push({
        label: "Overnight lows",
        value: (s.totalHypoCount ?? 0).toString(),
        unit: "lows",
        note: lowsCaption,
      });
    }
    return figures;
  });
</script>

<svelte:head>
  <title>Sleep & Overnight - Nocturne Reports</title>
  <meta
    name="description"
    content="Sleep pattern actogram with glucose overlay"
  />
</svelte:head>

<div class="@container container mx-auto space-y-6 p-3 @md:p-6 max-w-7xl">
  <div class="print:hidden">
    <h1 class="text-2xl @md:text-3xl font-bold">Sleep & Overnight</h1>
    <p class="text-muted-foreground">
      Sleep patterns with overnight glucose overlay
    </p>
  </div>


  {#if fullyEmpty}
    <Card>
      <CardContent class="p-12 text-center">
        <Artwork
          artwork="crescent-moon"
          palette="moonlight"
          motion="auto"
          autoplay="once"
          class="mx-auto mb-4 size-48"
        />
        <h2 class="mb-2 text-xl font-semibold">No sleep data</h2>
        <p class="mx-auto max-w-md text-muted-foreground">
          Sleep sessions arrive from connected sources (Apple Health, Health
          Connect, Fitbit, Oura, Garmin, Samsung) or manual entries. If
          tracking started recently, try a larger date range.
        </p>
      </CardContent>
    </Card>
  {:else}
    <FigureStrip figures={sleepFigures} />
    {#if overnightFigures.length > 0}
      <FigureStrip figures={overnightFigures} />
    {/if}

    {#if hasTrendsNights && sleepWeeks.length > 0}
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <CalendarRange class="h-5 w-5 text-report-lifestyle" />
            Weekly Breakdown
          </CardTitle>
          <CardDescription class="print:hidden">Each tracked night links to its full report.</CardDescription>
        </CardHeader>
        <CardContent>
          <SleepWeeklyBreakdown weeks={sleepWeeks} nights={sleepNights} />
        </CardContent>
      </Card>
    {/if}

    {#if showCompositionCard}
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Moon class="h-5 w-5 text-report-lifestyle" />
            Sleep Composition
          </CardTitle>
          {#if sourceFilter !== "all"}
            <CardDescription>
              Showing all {sourceLabel} sessions — nights aren't deduplicated across devices.
            </CardDescription>
          {:else}
            <CardDescription class="hidden print:block">Source: {sourceLabel}</CardDescription>
          {/if}
          <CardAction class="print:hidden">
            <Select.Root
              type="single"
              value={sourceFilter}
              onValueChange={(v) =>
                (viewParams.source = Object.values(SleepSource).find((s) => s === v) ?? null)}
            >
              <Select.Trigger class="w-44">
                {sourceLabel}
              </Select.Trigger>
              <Select.Content>
                {#each sourceOptions as opt (opt.value)}
                  <Select.Item value={opt.value} label={opt.label} />
                {/each}
              </Select.Content>
            </Select.Root>
          </CardAction>
        </CardHeader>
        <CardContent>
          <SleepCompositionChart
            nights={sleepNights}
            days={selectedRangeDays}
            meanDeepPct={sleepSummary?.meanDeepPct}
            meanRemPct={sleepSummary?.meanRemPct}
            referenceRanges={sleepSummary?.referenceRanges}
            deepMinutesDelta={sleepSummary?.last7dVsPrior7d?.deepMinutesDelta}
          />
        </CardContent>
      </Card>
    {/if}

    <Card class="print:break-inside-auto!">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Moon class="h-5 w-5 text-report-lifestyle" />
          Sleep Actogram
        </CardTitle>
      </CardHeader>
      <CardContent class="w-full overflow-x-auto print:overflow-visible">
        <Actogram
          data={sleepPoints}
          bgData={bgPoints}
          days={report.days}
          thresholds={actogramResource.current?.thresholds}
          rowHeight={48}
          visibleCount={VISIBLE_DAYS}
          printCount={report.rangeDayCount}
          initialOffset={0}
          legend={actogramLegend}
        >
          {#snippet tooltipValue({ point })}
            {@const span = sleepSpanOf(point)}
            <div class="size-2 rounded-full bg-lane" data-lane={span.state.toLowerCase()}></div>
            <span class="text-muted-foreground">Sleep</span>
            <span class="ml-auto font-medium capitalize">{span.state.toLowerCase()}</span>
          {/snippet}
          {#snippet rowLabel({ day })}
            {@const linkDate = nightDateByDayKey.get(dayKeyFor(day))}
            {#if linkDate}
              <a
                href={resolve("/(authenticated)/reports/sleep/[date]", { date: linkDate })}
                class="block text-xs text-muted-foreground text-right pr-2 hover:text-foreground hover:underline"
              >
                {formatRowLabelDate(day)}
              </a>
            {:else}
              <span class="block text-xs text-muted-foreground text-right pr-2">
                {formatRowLabelDate(day)}
              </span>
            {/if}
          {/snippet}
          {#snippet row(ctx: ActogramRowContext)}
            {#each ctx.data as { point, hoursFromStart, isExtended }, i (i)}
              {@const span = sleepSpanOf(point)}
              {@const durationHours = (span.endMills - span.startMills) / MS_PER_HOUR}
              {@const x = ctx.xScale(new Date(ctx.day.getTime() + hoursFromStart * MS_PER_HOUR))}
              {@const endHours = hoursFromStart + durationHours}
              {@const clampedEnd = Math.min(endHours, HOURS_PER_ROW)}
              {@const x2 = ctx.xScale(new Date(ctx.day.getTime() + clampedEnd * MS_PER_HOUR))}
              {@const rectWidth = Math.max(x2 - x, 1)}
              <rect
                {x}
                y={4}
                width={rectWidth}
                height={ctx.height - 8}
                data-lane={span.state.toLowerCase()}
                class={["fill-lane", patternClass(laneTexture(laneForStage(span.state))), !isExtended && "print:opacity-90"]}
                opacity={isExtended ? 0.25 : 0.5}
              />
            {/each}
          {/snippet}
        </Actogram>
      </CardContent>
    </Card>
  {/if}
</div>
