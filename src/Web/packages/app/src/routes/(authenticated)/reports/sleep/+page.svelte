<script lang="ts">
  import {
    Card,
    CardAction,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import * as Select from "$lib/components/ui/select";
  import {
    Moon,
    Calendar,
    CalendarRange,
    Sparkles,
    Gauge,
    Sunrise,
    TriangleAlert,
    Activity,
  } from "lucide-svelte";
  import {
    ACTOGRAM_PADDING_DAYS,
    Actogram,
    buildDayRange,
    type ActogramRowContext,
  } from "$lib/components/actogram";
  import { MS_PER_DAY, MS_PER_HOUR, HOURS_PER_ROW } from "$lib/components/actogram/actogram";
  import { getActogramData } from "$api/actogram.remote";
  import { getTrends } from "$api/generated/sleepReports.generated.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { resolve } from "$app/paths";
  import { dayKeyFor, buildNightsByDayKey } from "$lib/utils/sleep-night-mapping";
  import { bgDelta, bgLabel } from "$lib/utils/formatting";
  import SleepSummaryTile, {
    type TileDelta,
  } from "$lib/components/reports/sleep/SleepSummaryTile.svelte";
  import SleepCompositionChart from "$lib/components/reports/sleep/SleepCompositionChart.svelte";
  import SleepWeeklyBreakdown from "$lib/components/reports/sleep/SleepWeeklyBreakdown.svelte";
  import { SleepSource } from "$api";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";

  const VISIBLE_DAYS = 14;

  const reportsParams = requireDateParamsContext(14);

  /** Padded window the actogram loads, so its double-plot rows have context either side. */
  const paddedRangeMillis = $derived({
    from: reportsParams.dateRangeMillis.from - ACTOGRAM_PADDING_DAYS * MS_PER_DAY,
    to: reportsParams.dateRangeMillis.to + ACTOGRAM_PADDING_DAYS * MS_PER_DAY,
  });

  const actogramResource = contextResource(
    () =>
      getActogramData({
        from: paddedRangeMillis.from,
        to: paddedRangeMillis.to,
      }),
    { errorTitle: "Error Loading Sleep Report" }
  );

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
        from: new Date(reportsParams.dateRangeMillis.from),
        to: new Date(reportsParams.dateRangeMillis.to),
        source: sourceFilter === "all" ? undefined : sourceFilter,
      }),
    { errorTitle: "Error Loading Sleep Report" }
  );

  // Actogram rows run newest-first (most recent night on top), descending into
  // the past. Anchored at the selected range end — never the future — so padding
  // days after `to` are not shown as rows; the padded fetch window above still
  // feeds each row's next-day double-plot. Extends ACTOGRAM_PADDING_DAYS before
  // the range start for scroll-back context.
  const days = $derived(
    buildDayRange(
      reportsParams.dateRangeMillis.from - ACTOGRAM_PADDING_DAYS * MS_PER_DAY,
      reportsParams.dateRangeMillis.to
    ).reverse()
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

  // BG data as GlucosePoints
  const bgPoints = $derived(
    (actogramResource.current?.glucoseData ?? []).map((g) => ({ mills: g.mills, sgv: g.sgv, color: g.color }))
  );

  const sleepSummary = $derived(trendsResource.current?.summary);
  const sleepNights = $derived(trendsResource.current?.nights ?? []);
  const sleepWeeks = $derived(trendsResource.current?.weeks ?? []);

  // Maps each display day to the night's authoritative display date, for actogram row links.
  const nightDateByDayKey = $derived.by(() => {
    const map = new Map<number, string>();
    for (const [key, night] of buildNightsByDayKey(sleepNights)) {
      if (night.displayDate) map.set(key, night.displayDate);
    }
    return map;
  });

  function formatHoursMinutes(hours: number): string {
    const totalMinutes = Math.round(hours * 60);
    return `${Math.floor(totalMinutes / 60)}h ${totalMinutes % 60}m`;
  }

  function formatRowLabelDate(day: Date): string {
    return day.toLocaleDateString(undefined, { month: "short", day: "numeric" });
  }

  // --- Empty-state detection -------------------------------------------------
  const hasActogramSleep = $derived(sleepPoints.length > 0);
  const hasTrendsNights = $derived((sleepSummary?.nightCount ?? 0) > 0);
  const fullyEmpty = $derived(!hasTrendsNights && !hasActogramSleep);
  const showCompositionCard = $derived(hasTrendsNights);

  // --- Delta arrows (adapted from the comparison report's diffRows) ---------
  function signedNumber(value: number, digits = 0): string {
    const abs = Math.abs(value);
    if (abs < (digits === 0 ? 0.5 : 0.05)) return "±0";
    const sign = value > 0 ? "+" : "−";
    return `${sign}${abs.toFixed(digits)}`;
  }

  /** goodWhen="up": positive deltas render as an improvement (in-range green). */
  function directionalDelta(
    value: number | null | undefined,
    goodWhen: "up" | "down",
    format: (v: number) => string,
    digits = 0
  ): TileDelta | null {
    if (value == null) return null;
    const flat = Math.abs(value) < (digits === 0 ? 0.5 : 0.05);
    const direction: TileDelta["direction"] = flat ? "flat" : value > 0 ? "up" : "down";
    const tone: TileDelta["tone"] = flat
      ? "neutral"
      : (goodWhen === "up") === (direction === "up")
        ? "good"
        : "bad";
    return { text: format(value), direction, tone, title: "vs prior 7 nights" };
  }

  /** Always muted — direction of dawn rise isn't colored as better/worse. */
  function neutralDelta(
    value: number | null | undefined,
    format: (v: number) => string
  ): TileDelta | null {
    if (value == null) return null;
    const direction: TileDelta["direction"] =
      Math.abs(value) < 0.05 ? "flat" : value > 0 ? "up" : "down";
    return { text: format(value), direction, tone: "neutral", title: "vs prior 7 nights" };
  }

  const scoreDelta = $derived(
    directionalDelta(sleepSummary?.last7dVsPrior7d?.scoreDelta, "up", (v) => signedNumber(v))
  );
  const tirDelta = $derived(
    directionalDelta(sleepSummary?.last7dVsPrior7d?.tirDelta, "up", (v) => `${signedNumber(v)} pp`)
  );
  const dawnDelta = $derived(
    neutralDelta(
      sleepSummary?.last7dVsPrior7d?.dawnRiseDelta,
      (v) => `${bgDelta(v)} ${bgLabel()}`
    )
  );

  // --- Tile captions -----------------------------------------------------
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
</script>

<svelte:head>
  <title>Sleep & Overnight - Nocturne Reports</title>
  <meta
    name="description"
    content="Sleep pattern actogram with glucose overlay"
  />
</svelte:head>

{#await actogramResource then actogramData}
  {#if actogramData}
  <div class="@container container mx-auto space-y-6 p-3 @md:p-6 max-w-7xl">
    <!-- Header -->
    <div>
      <h1 class="text-2xl @md:text-3xl font-bold">Sleep & Overnight</h1>
      <p class="text-muted-foreground">
        Sleep patterns with overnight glucose overlay
      </p>
    </div>

    {#if fullyEmpty}
      <Card>
        <CardContent class="p-12 text-center">
          <div
            class="mx-auto mb-4 flex h-20 w-20 items-center justify-center rounded-full bg-muted"
          >
            <Moon class="h-10 w-10 text-muted-foreground" />
          </div>
          <h2 class="mb-2 text-xl font-semibold">No sleep data</h2>
          <p class="mx-auto max-w-md text-muted-foreground">
            Sleep sessions arrive from connected sources (Apple Health, Health
            Connect, Fitbit, Oura, Garmin, Samsung) or manual entries. If
            tracking started recently, try a larger date range.
          </p>
        </CardContent>
      </Card>
    {:else}
      <!-- Summary Cards -->
      <div class="grid grid-cols-2 @sm:grid-cols-4 gap-4">
        {#if hasTrendsNights}
          <SleepSummaryTile
            icon={Moon}
            iconClass="text-indigo-500"
            label="Average Sleep"
            value={formatHoursMinutes((sleepSummary?.meanAsleepMinutes ?? 0) / 60)}
            caption="per night"
          />
        {/if}

        <SleepSummaryTile
          icon={Calendar}
          label="Nights Tracked"
          value={`${sleepSummary?.nightCount ?? 0} of ${sleepSummary?.daysInRange ?? 0}`}
          caption={nightsTrackedCaption}
        />

        {#if sleepSummary?.meanScore != null}
          <SleepSummaryTile
            icon={Sparkles}
            label="Sleep Score"
            value={Math.round(sleepSummary.meanScore).toString()}
            caption={scoreCaption}
            delta={scoreDelta}
          />
        {/if}

        {#if sleepSummary?.meanTirPct != null}
          <SleepSummaryTile
            icon={Gauge}
            label="Overnight TIR"
            value={`${Math.round(sleepSummary.meanTirPct)}%`}
            caption={tirCaption}
            delta={tirDelta}
          />
        {/if}

        {#if sleepSummary?.meanDawnRiseMg != null}
          <SleepSummaryTile
            icon={Sunrise}
            label="Dawn Rise"
            value={bgDelta(sleepSummary.meanDawnRiseMg, true)}
            unit={bgLabel()}
            caption="avg pre-wake change"
            delta={dawnDelta}
          />
        {/if}

        <!-- meanTirPct is non-null exactly when overnight CGM data exists; hypo
             counts are only meaningful on CGM nights, so the lows tile shares
             the TIR tile's gate. -->
        {#if sleepSummary?.meanTirPct != null}
          <SleepSummaryTile
            icon={TriangleAlert}
            label="Overnight Lows"
            value={(sleepSummary?.totalHypoCount ?? 0).toString()}
            caption={lowsCaption}
          />
        {/if}

        {#if sleepSummary?.meanHrvMs != null}
          <SleepSummaryTile
            icon={Activity}
            label="HRV"
            value={Math.round(sleepSummary.meanHrvMs).toString()}
            unit="ms"
            caption="overnight average"
          />
        {/if}
      </div>

      {#if hasTrendsNights && sleepWeeks.length > 0}
        <!-- Weekly Breakdown -->
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <CalendarRange class="h-5 w-5 text-indigo-500" />
              Weekly Breakdown
            </CardTitle>
            <CardDescription>Each tracked night links to its full report.</CardDescription>
          </CardHeader>
          <CardContent>
            <SleepWeeklyBreakdown weeks={sleepWeeks} nights={sleepNights} />
          </CardContent>
        </Card>
      {/if}

      {#if showCompositionCard}
        <!-- Sleep Composition -->
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <Moon class="h-5 w-5 text-indigo-500" />
              Sleep Composition
            </CardTitle>
            {#if sourceFilter !== "all"}
              <CardDescription>
                Showing all {sourceLabel} sessions — nights aren't deduplicated across devices.
              </CardDescription>
            {/if}
            <CardAction>
              <Select.Root
                type="single"
                value={sourceFilter}
                onValueChange={(v) =>
                  (viewParams.source = v && v !== "all" ? (v as SleepSource) : null)}
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

      <!-- Actogram -->
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Moon class="h-5 w-5 text-indigo-500" />
            Sleep Actogram
          </CardTitle>
        </CardHeader>
        <CardContent class="w-full overflow-x-auto print:overflow-visible">
          <Actogram
            data={sleepPoints}
            bgData={bgPoints}
            {days}
            thresholds={actogramResource.current?.thresholds}
            rowHeight={48}
            visibleCount={VISIBLE_DAYS}
            initialOffset={0}
          >
            {#snippet tooltipValue({ point })}
              {@const span = point as { mills: number; state: string }}
              <div class="size-2 rounded-full bg-[var(--lane-color)]" data-lane={span.state.toLowerCase()}></div>
              <span class="text-muted-foreground">Sleep</span>
              <span class="ml-auto font-mono font-medium tabular-nums capitalize">{span.state.toLowerCase()}</span>
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
              {#each ctx.data as { point, hoursFromStart, isExtended }}
                {@const span = point as { mills: number; startMills: number; endMills: number; state: string }}
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
                  class="fill-[var(--lane-color)]"
                  opacity={isExtended ? 0.25 : 0.5}
                />
              {/each}
            {/snippet}
          </Actogram>
        </CardContent>
      </Card>
    {/if}
  </div>
  {/if}
{/await}
