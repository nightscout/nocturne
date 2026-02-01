<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Layers,
    Calendar,
    Info,
    TrendingUp,
    TrendingDown,
    ArrowRight,
    Printer,
    HelpCircle,
    Clock,
    Gauge,
  } from "lucide-svelte";
  import BasalRatePercentileChart from "$lib/components/reports/BasalRatePercentileChart.svelte";
  import InsulinDeliveryChart from "$lib/components/reports/InsulinDeliveryChart.svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { getBasalAnalysis } from "$lib/data/statistics.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days for basal pattern analysis
  const reportsParams = requireDateParamsContext(14);

  // Use contextResource - it syncs to layout's ResourceGuard automatically
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Basal Analysis" }
  );

  // Derived data from reportsResource
  const treatments = $derived(reportsResource.current?.treatments ?? []);
  const basalSeries = $derived(reportsResource.current?.basalSeries ?? []);
  const dateRange = $derived(
    reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
    }
  );

  // Helper dates
  const startDate = $derived(new Date(dateRange.from));
  const endDate = $derived(new Date(dateRange.to));
  const dayCount = $derived(
    Math.max(
      1,
      Math.round(
        (endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24)
      )
    )
  );

  // Secondary query for basal analysis
  const basalAnalysisQuery = $derived(
    getBasalAnalysis({
      startDate: dateRange.from,
      endDate: dateRange.to,
    })
  );

  // Get stats and tempBasalInfo from backend response with explicit defaults
  const basalStats = $derived({
    count: basalAnalysisQuery.current?.stats?.count ?? 0,
    avgRate: basalAnalysisQuery.current?.stats?.avgRate ?? 0,
    minRate: basalAnalysisQuery.current?.stats?.minRate ?? 0,
    maxRate: basalAnalysisQuery.current?.stats?.maxRate ?? 0,
    totalDelivered: basalAnalysisQuery.current?.stats?.totalDelivered ?? 0,
  });

  const tempBasalInfo = $derived({
    total: basalAnalysisQuery.current?.tempBasalInfo?.total ?? 0,
    perDay: basalAnalysisQuery.current?.tempBasalInfo?.perDay ?? 0,
    highTemps: basalAnalysisQuery.current?.tempBasalInfo?.highTemps ?? 0,
    lowTemps: basalAnalysisQuery.current?.tempBasalInfo?.lowTemps ?? 0,
    zeroTemps: basalAnalysisQuery.current?.tempBasalInfo?.zeroTemps ?? 0,
  });

  const hourlyPercentiles = $derived(
    basalAnalysisQuery.current?.hourlyPercentiles ?? []
  );

  // Loading state for child components
  const isLoading = $derived(reportsResource.loading || basalAnalysisQuery.loading);
</script>

<svelte:head>
  <title>Basal Rate Analysis - Nocturne Reports</title>
  <meta
    name="description"
    content="Analyze your basal insulin delivery patterns with percentile visualization"
  />
</svelte:head>

{#if reportsResource.current}
<div class="container mx-auto max-w-7xl space-y-8 px-4 py-6">
  <!-- Header -->
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 class="flex items-center gap-3 text-3xl font-bold">
          <Layers class="h-8 w-8 text-amber-600" />
          Basal Rate Analysis
        </h1>
        <p class="mt-1 text-muted-foreground">
          Understand your background insulin delivery patterns over time
        </p>
      </div>
      <div class="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          class="gap-2"
          onclick={() => window.print()}
        >
          <Printer class="h-4 w-4" />
          Print
        </Button>
        <Button
          href="/reports/insulin-delivery"
          variant="outline"
          size="sm"
          class="gap-2"
        >
          Insulin Delivery
          <ArrowRight class="h-4 w-4" />
        </Button>
      </div>
    </div>

    <!-- Period info -->
    <div class="flex items-center gap-2 text-sm text-muted-foreground">
      <Calendar class="h-4 w-4" />
      <span>
        {startDate.toLocaleDateString()} – {endDate.toLocaleDateString()}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{dayCount} days</span>
      <span class="text-muted-foreground/50">•</span>
      <span>{basalStats.count} basal events</span>
    </div>
  </div>

  <!-- What is this report - Educational Card -->
  <Card
    class="border-2 border-amber-200 bg-amber-50/50 dark:border-amber-800 dark:bg-amber-950/30"
  >
    <CardHeader class="pb-3">
      <CardTitle class="flex items-center gap-2 text-base">
        <HelpCircle class="h-5 w-5 text-amber-600" />
        Understanding This Report
      </CardTitle>
    </CardHeader>
    <CardContent class="space-y-2 text-sm">
      <p>
        This report shows how your <strong>
          basal (background) insulin delivery
        </strong>
        varies throughout the day. The percentile chart shows your typical patterns:
      </p>
      <ul class="list-inside list-disc space-y-1 pl-2 text-muted-foreground">
        <li>
          The <strong>median line</strong>
          shows your most common basal rate at each hour
        </li>
        <li>
          The <strong>shaded bands</strong>
          show the range of variation (10th-90th percentile)
        </li>
        <li>Wider bands indicate more variability in your basal needs</li>
      </ul>
      <p class="text-muted-foreground">
        Use this to identify times when your basal insulin may need adjustment,
        or to discuss temp basal patterns with your healthcare provider.
      </p>
    </CardContent>
  </Card>

  <!-- Key Stats Cards -->
  <div class="grid grid-cols-2 gap-4 md:grid-cols-4">
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {basalStats.avgRate.toFixed(2)}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg Rate</div>
        <div class="text-[10px] text-muted-foreground/60">U/hr</div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {basalStats.totalDelivered.toFixed(1)}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Total Basal</div>
        <div class="text-[10px] text-muted-foreground/60">units delivered</div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {tempBasalInfo.perDay.toFixed(1)}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Temp Basals</div>
        <div class="text-[10px] text-muted-foreground/60">per day avg</div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="flex items-center justify-center gap-2">
          <span class="text-lg font-bold text-green-600">
            {tempBasalInfo.highTemps}
          </span>
          <span class="text-muted-foreground">/</span>
          <span class="text-lg font-bold text-red-600">
            {tempBasalInfo.lowTemps}
          </span>
        </div>
        <div class="text-xs font-medium text-muted-foreground">High / Low</div>
        <div class="text-[10px] text-muted-foreground/60">temp basals</div>
      </CardContent>
    </Card>
  </div>

  <!-- Main Percentile Chart -->
  <Card class="border">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Gauge class="h-5 w-5 text-muted-foreground" />
        Basal Rate Percentile Chart
      </CardTitle>
      <CardDescription>
        Your typical basal delivery pattern across 24 hours (like an AGP for
        basal rates)
      </CardDescription>
    </CardHeader>
    <CardContent>
      <BasalRatePercentileChart data={hourlyPercentiles} loading={isLoading} />
    </CardContent>
  </Card>

  <!-- Hourly Insulin Delivery -->
  <Card class="border">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Clock class="h-5 w-5 text-muted-foreground" />
        Average Hourly Basal Delivery
      </CardTitle>
      <CardDescription>
        Average basal insulin delivered per hour of the day
      </CardDescription>
    </CardHeader>
    <CardContent>
      <InsulinDeliveryChart {treatments} {basalSeries} showStacked={false} />
    </CardContent>
  </Card>

  <!-- Insights Section -->
  {#if basalStats.count > 0}
    <Card class="border">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Info class="h-5 w-5 text-muted-foreground" />
          Basal Insights
        </CardTitle>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 md:grid-cols-2">
          <!-- Rate Range -->
          <div class="rounded-lg border bg-card p-4">
            <div class="flex items-start gap-3">
              <div class="rounded-lg bg-primary/10 p-2">
                <TrendingUp class="h-4 w-4 text-primary" />
              </div>
              <div>
                <h4 class="font-medium">Basal Rate Range</h4>
                <p class="text-sm text-muted-foreground">
                  Your basal rates ranged from <strong>
                    {basalStats.minRate.toFixed(2)} U/hr
                  </strong>
                  to
                  <strong>{basalStats.maxRate.toFixed(2)} U/hr</strong>
                  .
                  {#if basalStats.maxRate - basalStats.minRate > 0.5}
                    This indicates significant variation in your basal needs
                    throughout the day.
                  {:else}
                    Your basal rates are relatively consistent.
                  {/if}
                </p>
              </div>
            </div>
          </div>

          <!-- Temp Basal Usage -->
          <div class="rounded-lg border bg-card p-4">
            <div class="flex items-start gap-3">
              <div class="rounded-lg bg-amber-500/10 p-2">
                <Layers class="h-4 w-4 text-amber-600" />
              </div>
              <div>
                <h4 class="font-medium">Temp Basal Activity</h4>
                <p class="text-sm text-muted-foreground">
                  {#if tempBasalInfo.perDay > 10}
                    High temp basal activity ({tempBasalInfo.perDay.toFixed(
                      1
                    )}/day) suggests active automated or manual adjustments.
                  {:else if tempBasalInfo.perDay > 3}
                    Moderate temp basal activity — typical for automated insulin
                    delivery systems.
                  {:else if tempBasalInfo.perDay > 0}
                    Low temp basal activity — your basal rates may be
                    well-tuned.
                  {:else}
                    No temp basal activity recorded in this period.
                  {/if}
                </p>
              </div>
            </div>
          </div>

          <!-- Zero Temps -->
          {#if tempBasalInfo.zeroTemps > 0}
            <div
              class="rounded-lg border border-red-200 bg-red-50 p-4 dark:border-red-800 dark:bg-red-950/30"
            >
              <div class="flex items-start gap-3">
                <div class="rounded-lg bg-red-500/10 p-2">
                  <TrendingDown class="h-4 w-4 text-red-600" />
                </div>
                <div>
                  <h4 class="font-medium text-red-700 dark:text-red-400">
                    Suspend/Zero Temp Basals
                  </h4>
                  <p class="text-sm text-muted-foreground">
                    <strong>{tempBasalInfo.zeroTemps}</strong>
                    zero or suspend temp basals were recorded. This often indicates
                    low glucose prevention or manual suspensions.
                  </p>
                </div>
              </div>
            </div>
          {/if}

          <!-- Daily Average -->
          <div class="rounded-lg border bg-card p-4">
            <div class="flex items-start gap-3">
              <div class="rounded-lg bg-blue-500/10 p-2">
                <Calendar class="h-4 w-4 text-blue-600" />
              </div>
              <div>
                <h4 class="font-medium">Daily Basal Insulin</h4>
                <p class="text-sm text-muted-foreground">
                  Average of <strong>
                    {(basalStats.totalDelivered / dayCount).toFixed(1)} units
                  </strong>
                  of basal insulin delivered per day over this {dayCount}-day
                  period.
                </p>
              </div>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  {/if}

  <!-- Navigation -->
  <Separator />
  <div class="flex flex-wrap items-center justify-center gap-2">
    <Button href="/reports" variant="outline" size="sm">← All Reports</Button>
    <Button href="/reports/insulin-delivery" size="sm" class="gap-2">
      Insulin Delivery Report
      <ArrowRight class="h-4 w-4" />
    </Button>
    <Button href="/reports/treatments" variant="outline" size="sm">
      Treatment Log
    </Button>
  </div>

  <!-- Footer -->
  <div class="space-y-1 text-center text-xs text-muted-foreground">
    <p>
      Report generated from {treatments.length.toLocaleString()} treatments between
      {startDate.toLocaleDateString()} and {endDate.toLocaleDateString()}
    </p>
    <p class="text-muted-foreground/60">
      This report is for informational purposes only. Always consult your
      healthcare provider for medical advice.
    </p>
  </div>
</div>
{/if}
