<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    Gauge,
    AlertTriangle,
    ArrowRight,
    BarChart3,
    Calendar,
    ChevronRight,
  } from "lucide-svelte";
  import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "$lib/components/ui/card";
  import { page } from "$app/state";
  import {
    reportsOverviewScopes,
    visibleReportCategories,
  } from "$lib/navigation/report-navigation.svelte";
  import { satisfiesAllScopes } from "$lib/authorization/scopes";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import { getReportsData } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseRange, formatGlucoseValue, formatLocale, formatNumber, formatShortDate, getUnitLabel } from "$lib/utils/formatting";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { coachmark } from "@nocturne/coach";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is standard for reports overview
  const reportsParams = requireDateParamsContext(14);

  const grantedScopes: string[] = $derived(
    page.data.effectivePermissions ?? []
  );
  const viewer = $derived({
    grantedScopes,
    anonymous: !page.data.user,
  });
  const categories = $derived(visibleReportCategories(viewer));
  const visibleHrefs = $derived(
    new Set(categories.flatMap((c) => c.reports).map((r) => r.href))
  );

  const canLoadSummary = $derived(
    satisfiesAllScopes(grantedScopes, reportsOverviewScopes)
  );

  // A viewer without the summary's scopes gets no query at all: the analytics call would
  // 403 and the page would render its error state instead of the reports it can open.
  const reportsResource = contextResource(
    () =>
      canLoadSummary
        ? getReportsData(reportsParams.dateRangeInput)
        : { loading: false, error: null, current: undefined, refresh: () => {} },
    { errorTitle: "Error Loading Reports", dateParams: reportsParams }
  );

  const isLoading = $derived(reportsResource.loading);
  const queryData = $derived(reportsResource.current);
  const entries = $derived(queryData?.entries ?? []);
  const analysis = $derived(queryData?.analysis);
  const averagedStats = $derived(queryData?.averagedStats);
  const startDate = $derived(reportsResource.date.from);
  const endDate = $derived(reportsResource.date.to);
  const lastUpdated = $derived(queryData?.dateRange?.lastUpdated);

  const units = $derived(glucoseUnits.current);
  const glucoseFormatting = $derived({
    unitLabel: getUnitLabel(units),
    targetRangeDisplay: formatGlucoseRange(70, 180, units),
  });

  const tir = $derived(analysis?.timeInRange?.percentages);
  const variability = $derived(analysis?.glycemicVariability);
  const stats = $derived(analysis?.basicStats);

  // Personal target range schedule, shown as an overlay on the clinical TIR chart alongside —
  // not instead of — the ATTD consensus bands. Absent when the tenant has no schedule
  // configured or the window has no readings. Markers are only drawn for single-entry
  // schedules: with a time-of-day-varying range, cumulative-time positions don't correspond
  // to glucose boundaries on the value-sorted bar, so those get the caption only.
  const personalRangeOverlay = $derived.by(() => {
    const personalRange = queryData?.personalRange;
    if (!personalRange?.entries?.length) return undefined;
    const [firstEntry] = personalRange.entries;
    const singleEntry =
      personalRange.entries.length === 1 &&
      firstEntry.low !== undefined &&
      firstEntry.high !== undefined;
    const rangeLabel = singleEntry
      ? formatGlucoseRange(firstEntry.low!, firstEntry.high!, units)
      : "your schedule";
    return {
      belowPercent: singleEntry ? personalRange.belowRangePercent : undefined,
      abovePercent: singleEntry ? personalRange.aboveRangePercent : undefined,
      label: `Your range: ${rangeLabel} · ${Math.round(personalRange.inRangePercent ?? 0)}% of time`,
    };
  });

  const CATEGORY_ICON_CLASS = {
    overview: "text-report-overview",
    patterns: "text-report-patterns",
    lifestyle: "text-report-lifestyle",
    treatment: "text-report-treatment",
  } as const;
</script>

<svelte:head>
  <title>Reports - Nocturne</title>
  <meta
    name="description"
    content="Comprehensive diabetes management analytics and insights"
  />
</svelte:head>

{#if isLoading && !reportsResource.current}
  <ReportsSkeleton />
{:else if reportsResource.error}
  <div class="flex min-h-[60vh] items-center justify-center px-4">
    <div class="max-w-md space-y-4 text-center">
      <AlertTriangle class="mx-auto size-6 text-destructive" aria-hidden="true" />
      <h2 class="text-xl font-semibold">Unable to load reports</h2>
      <p class="text-muted-foreground">
        {remoteErrorMessage(reportsResource.error, "Something went wrong")}
      </p>
      <Button variant="outline" onclick={() => reportsResource.refresh()}>
        Try again
      </Button>
    </div>
  </div>
{:else}
  <div class="@container mx-auto max-w-6xl space-y-10 px-3 py-6 @md:px-6">
    <header class="flex flex-col gap-4 @3xl:flex-row @3xl:items-end @3xl:justify-between">
      <div>
        <h1 class="text-3xl font-bold tracking-tight">Reports</h1>
        <p class="mt-1 text-muted-foreground tabular-nums">
          {formatShortDate(startDate)} – {formatShortDate(endDate, true)}
          {#if canLoadSummary}
            · {formatNumber(entries.length)} readings
          {/if}
        </p>
      </div>
      <div class="flex flex-wrap gap-2">
        {#if visibleHrefs.has("/reports/executive-summary")}
          <Button href="/reports/executive-summary">
            <Gauge />
            Executive Summary
          </Button>
        {/if}
        {#if visibleHrefs.has("/reports/agp")}
          <Button href="/reports/agp" variant="outline">
            <BarChart3 />
            AGP Report
          </Button>
        {/if}
        {#if visibleHrefs.has("/reports/readings")}
          <Button href="/reports/readings" variant="outline">
            <Calendar />
            Day-by-Day
          </Button>
        {/if}
      </div>
    </header>

    {#if canLoadSummary}
      {#if analysis}
        {@const tirValue = tir?.target}
        <Card size="flush">
          <div class="grid @3xl:grid-cols-[minmax(0,5fr)_minmax(0,7fr)]">
            <div class="flex flex-col gap-4 p-6">
              <h2 class="text-sm font-medium text-muted-foreground">Time in range</h2>
              {#if tirValue != null}
                <div class="h-64 @sm:h-72">
                  <TIRStackedChart percentages={tir} personalRange={personalRangeOverlay} showThresholds />
                </div>
              {:else}
                <p class="text-xl text-muted-foreground">No data</p>
              {/if}
              <p class="text-sm text-muted-foreground">Consensus target: at least 70% of time in range.</p>
              {#if analysis?.reliability?.meetsReliabilityCriteria === false}
                <ReliabilityBadge reliability={analysis.reliability} />
              {/if}
            </div>

            <dl class="m-0 divide-y divide-border border-t border-border @3xl:border-t-0 @3xl:border-l">
              <div class="flex items-baseline justify-between gap-4 px-6 py-4">
                <dt class="text-sm text-muted-foreground">Average glucose</dt>
                <dd class="m-0 text-lg font-semibold tabular-nums">
                  {stats?.mean ? formatGlucoseValue(stats.mean, units) : "–"}
                  <span class="text-sm font-normal text-muted-foreground">{glucoseFormatting.unitLabel}</span>
                </dd>
              </div>
              <div class="flex items-baseline justify-between gap-4 px-6 py-4">
                <dt class="text-sm text-muted-foreground">Estimated A1C</dt>
                <dd class="m-0 text-lg font-semibold tabular-nums">
                  {variability?.estimatedA1c?.toFixed(1) ?? "–"}<span class="text-sm font-normal text-muted-foreground">%</span>
                </dd>
              </div>
              <div class="flex items-baseline justify-between gap-4 px-6 py-4">
                <dt class="text-sm text-muted-foreground">Coefficient of variation</dt>
                <dd class="m-0 text-lg font-semibold tabular-nums">
                  {variability?.coefficientOfVariation?.toFixed(0) ?? "–"}<span class="text-sm font-normal text-muted-foreground">%</span>
                </dd>
              </div>
              <div class="flex items-baseline justify-between gap-4 px-6 py-4">
                <dt class="text-sm text-muted-foreground">Time below range</dt>
                <dd class="m-0 text-lg font-semibold tabular-nums">
                  {((tir?.low ?? 0) + (tir?.veryLow ?? 0)).toFixed(1)}<span class="text-sm font-normal text-muted-foreground">%</span>
                </dd>
              </div>
            </dl>
          </div>
        </Card>

        <Card>
          <CardHeader class="flex flex-row items-start justify-between gap-4">
            <div>
              <CardTitle>Typical day</CardTitle>
              <CardDescription>Glucose over 24 hours, across the whole range</CardDescription>
            </div>
            <Button href="/reports/agp" variant="ghost" size="sm">
              Full report
              <ArrowRight />
            </Button>
          </CardHeader>
          <CardContent>
            <div class="h-64">
              <AmbulatoryGlucoseProfile {averagedStats} />
            </div>
          </CardContent>
        </Card>
      {:else if !isLoading}
        <Card variant="dashed">
          <CardContent class="text-center">
            <h2 class="mb-2 text-lg font-semibold">No data in this range</h2>
            <p class="mx-auto max-w-md text-muted-foreground">
              There aren't enough glucose readings between these dates to
              summarise. Choose a longer date range.
            </p>
          </CardContent>
        </Card>
      {/if}
    {/if}

    <section
      class="grid gap-x-12 gap-y-10 @3xl:grid-cols-2"
      aria-label="All reports"
      {@attach coachmark({
        key: "setup-reports.categories",
        title: "Start with Executive Summary",
        description: "It combines your key metrics into a single page — great for clinic visits or sharing with your endo.",
        completeOn: { event: "click" },
      })}
    >
      {#each categories as category (category.id)}
        {@const CategoryIcon = category.icon}
        <div>
          <div class="mb-1 flex items-center gap-2">
            <CategoryIcon class="size-5 {CATEGORY_ICON_CLASS[category.id]}" aria-hidden="true" />
            <h2 class="text-lg font-semibold">{category.title}</h2>
          </div>
          <p class="mb-3 text-sm text-muted-foreground">{category.subtitle}</p>
          <ul class="m-0 list-none divide-y divide-border border-y border-border p-0">
            {#each category.reports as report (report.href)}
              <li>
                {#if report.status === "available"}
                  <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- report.href is a literal in-app path from report-navigation.svelte.ts -->
                  <a href={report.href}
                    class="group/report -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
                  >
                    <div class="min-w-0 flex-1">
                      <div class="font-medium text-foreground">{report.title}</div>
                      <div class="truncate text-sm text-muted-foreground">{report.description}</div>
                    </div>
                    <ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/report:translate-x-0.5" aria-hidden="true" />
                  </a>
                {:else}
                  <div class="flex items-center gap-3 py-3">
                    <div class="min-w-0 flex-1">
                      <div class="font-medium text-muted-foreground">{report.title}</div>
                      <div class="text-sm text-muted-foreground">Coming soon</div>
                    </div>
                  </div>
                {/if}
              </li>
            {/each}
          </ul>
        </div>
      {/each}
    </section>

    <footer class="space-y-1 text-sm text-muted-foreground">
      {#if lastUpdated}
        <p class="tabular-nums">
          Last updated {new Date(lastUpdated).toLocaleTimeString(formatLocale(), {
            hour: "2-digit",
            minute: "2-digit",
          })}
        </p>
      {/if}
      <p class="text-xs">
        This report is for informational purposes. Always consult your
        healthcare provider for medical advice.
      </p>
    </footer>
  </div>
{/if}
