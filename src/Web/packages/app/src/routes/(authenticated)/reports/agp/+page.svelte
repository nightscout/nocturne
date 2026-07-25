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
    BarChart3,
    Calendar,
    Target,
    TrendingUp,
    ArrowRight,
    Printer,
    HelpCircle,
  } from "lucide-svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import { getReportsData } from "$api/reports.remote";
  import { bg, bgLabel, bgRange } from "$lib/utils/formatting";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is the standard AGP report period
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration; `date` carries the
  // selected range so the header and footer never disagree with the query.
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading AGP Report", dateParams: reportsParams }
  );

  const entries = $derived(reportsResource.current?.entries ?? []);
  const analysis = $derived(reportsResource.current?.analysis);
  const averagedStats = $derived(reportsResource.current?.averagedStats);
  const lastUpdated = $derived(reportsResource.current?.dateRange?.lastUpdated);
  const startDate = $derived(reportsResource.date.from);
  const endDate = $derived(reportsResource.date.to);
  const dayCount = $derived(reportsResource.date.dayCount);
</script>

<svelte:head>
  <title>Ambulatory Glucose Profile - Nocturne Reports</title>
  <meta
    name="description"
    content="Standard AGP report with glucose pattern overlay, percentile bands, and time-in-range analysis"
  />
</svelte:head>

{#if reportsResource.current}
<div class="@container container mx-auto space-y-8 p-3 @md:p-6 max-w-7xl">
  <!-- Header with AGP Explanation -->
  <div class="space-y-4">
    <div class="flex items-center justify-between flex-wrap gap-4">
      <div>
        <h1 class="text-3xl font-bold flex items-center gap-3">
          <BarChart3 class="w-8 h-8 text-primary" />
          Ambulatory Glucose Profile
        </h1>
        <p class="text-muted-foreground mt-1">
          Your typical daily glucose pattern — a standardized clinical report
        </p>
      </div>
      <div class="flex items-center gap-2 print:hidden">
        <Button
          variant="outline"
          size="sm"
          class="gap-2"
          onclick={() => window.print()}
        >
          <Printer class="w-4 h-4" />
          Print
        </Button>
        <Button
          href="/reports/executive-summary"
          variant="outline"
          size="sm"
          class="gap-2"
        >
          Summary
          <ArrowRight class="w-4 h-4" />
        </Button>
      </div>
    </div>

    <!-- Period info -->
    <div class="flex items-center gap-2 text-sm text-muted-foreground">
      <Calendar class="w-4 h-4" />
      <span>
        {startDate.toLocaleDateString()} – {endDate.toLocaleDateString()}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{dayCount} days</span>
      <span class="text-muted-foreground/50">•</span>
      <span>{entries.length.toLocaleString()} readings</span>
    </div>
  </div>

  <!-- What is AGP - Educational Card -->
  <Card
    class="border-2 border-blue-200 dark:border-blue-800 bg-blue-50/50 dark:bg-blue-950/30"
  >
    <CardHeader class="pb-3">
      <CardTitle class="flex items-center gap-2 text-base">
        <HelpCircle class="w-5 h-5 text-blue-600" />
        What is an AGP?
      </CardTitle>
    </CardHeader>
    <CardContent class="text-sm space-y-2">
      <p>
        The <strong>Ambulatory Glucose Profile</strong>
        shows what a "typical" day looks like for your glucose levels. It overlays
        all your daily readings to reveal consistent patterns.
      </p>
      <details class="text-muted-foreground">
        <summary class="cursor-pointer text-blue-600 hover:underline">
          How to read this chart
        </summary>
        <div class="mt-2 space-y-2 pl-4 border-l-2 border-blue-200">
          <p>
            <strong>The dark line</strong>
            is your median (middle) glucose at each hour — what happens most often.
          </p>
          <p>
            <strong>The darker shaded area</strong>
            (25th-75th percentile) shows where you are 50% of the time.
          </p>
          <p>
            <strong>The lighter shaded area</strong>
            (10th-90th percentile) shows where you are 80% of the time.
          </p>
          <p>
            <strong>Green zone</strong>
            ({bgRange(70, 180)}) is the consensus target range. The consensus target
            is at least 70% of time in this zone.
          </p>
        </div>
      </details>
    </CardContent>
  </Card>

  <!-- Key Metrics Row -->
  {#if analysis}
    {@const tir = analysis.timeInRange?.percentages ?? {}}
    {@const stats = analysis.basicStats ?? {}}
    {@const variability = analysis.glycemicVariability ?? {}}

    <!-- Quick Stats Grid -->
    <div class="grid grid-cols-2 @lg:grid-cols-4 @3xl:grid-cols-6 gap-4">
      <Card
        class="p-4 text-center border-2 border-green-200 dark:border-green-800 bg-green-50/50 dark:bg-green-950/30"
      >
        <div class="text-3xl font-bold text-green-600">
          {tir.target?.toFixed(0) ?? "–"}%
        </div>
        <div class="text-xs text-muted-foreground">Time in Range</div>
        <div class="text-[10px] text-green-600">Target: ≥70%</div>
      </Card>
      <Card class="p-4 text-center">
        <div class="text-3xl font-bold">{stats.mean ? bg(stats.mean) : "–"}</div>
        <div class="text-xs text-muted-foreground">Average</div>
        <div class="text-[10px] text-muted-foreground/70">{bgLabel()}</div>
      </Card>
      <Card class="p-4 text-center">
        <div class="text-3xl font-bold text-red-600">
          {variability.estimatedA1c?.toFixed(1) ?? "–"}%
        </div>
        <div class="text-xs text-muted-foreground">Est. A1C</div>
        <div class="text-[10px] text-muted-foreground/70">GMI</div>
      </Card>
      <Card class="p-4 text-center">
        <div class="text-3xl font-bold text-purple-600">
          {variability.coefficientOfVariation?.toFixed(0) ?? "–"}%
        </div>
        <div class="text-xs text-muted-foreground">CV</div>
        <div class="text-[10px] text-purple-600">Target: ≤33%</div>
      </Card>
      <Card class="p-4 text-center">
        <div class="text-3xl font-bold text-red-500">
          {((tir.low ?? 0) + (tir.veryLow ?? 0)).toFixed(1)}%
        </div>
        <div class="text-xs text-muted-foreground">Below Range</div>
        <div class="text-[10px] text-red-500">Target: &lt;4%</div>
      </Card>
      <Card class="p-4 text-center">
        <div class="text-3xl font-bold text-orange-500">
          {((tir.high ?? 0) + (tir.veryHigh ?? 0)).toFixed(1)}%
        </div>
        <div class="text-xs text-muted-foreground">Above Range</div>
        <div class="text-[10px] text-orange-500">Target: &lt;25%</div>
      </Card>
    </div>

    <ReliabilityBadge reliability={analysis?.reliability} />

    <!-- Main AGP Chart -->
    <Card class="border-2">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <BarChart3 class="w-5 h-5" />
          Glucose Pattern (24-hour overlay)
        </CardTitle>
        <CardDescription>
          Median glucose with percentile bands showing your typical daily
          pattern
        </CardDescription>
      </CardHeader>
      <CardContent class="h-80 @lg:h-96 w-full">
        <AmbulatoryGlucoseProfile {averagedStats} />
      </CardContent>
    </Card>

    <!-- Time in Range Visual -->
    <div class="grid grid-cols-1 @3xl:grid-cols-2 gap-6">
      <Card class="border-2">
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Target class="w-5 h-5 text-green-600" />
            Time in Range Distribution
          </CardTitle>
          <CardDescription>
            How your time is distributed across glucose ranges
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4 py-4 h-48">
          <TIRStackedChart percentages={tir} />
        </CardContent>
      </Card>

      <!-- Key Patterns / Insights -->
      <Card class="border-2">
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <TrendingUp class="w-5 h-5 text-purple-600" />
            Measured Against Consensus Targets
          </CardTitle>
          <CardDescription>
            Each figure from this window next to the international consensus
            target for it
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          {@const observations = [
            {
              label: "Time in target range",
              value: tir.target,
              format: (v: number) => `${v.toFixed(0)}%`,
              target: "at least 70%",
            },
            {
              label: "Coefficient of variation (CV)",
              value: variability.coefficientOfVariation,
              format: (v: number) => `${v.toFixed(0)}%`,
              target: "33% or below",
            },
            {
              label: `Time below ${bg(70)} ${bgLabel()}`,
              value:
                tir.low != null || tir.veryLow != null
                  ? (tir.low ?? 0) + (tir.veryLow ?? 0)
                  : undefined,
              format: (v: number) => `${v.toFixed(1)}%`,
              target: "under 4%",
            },
          ]}
          {#each observations as observation (observation.label)}
            <div
              class="flex flex-wrap items-baseline justify-between gap-2 rounded-lg bg-muted/50 p-3"
            >
              <div>
                <p class="font-medium">{observation.label}</p>
                <p class="text-sm text-muted-foreground">
                  Consensus target: {observation.target}
                </p>
              </div>
              <p class="text-2xl font-bold tabular-nums">
                {observation.value != null
                  ? observation.format(observation.value)
                  : "No data"}
              </p>
            </div>
          {/each}
          <p class="text-xs text-muted-foreground">
            The percentile bands above show when in the day variation and
            excursions occur. Discuss any patterns with your care team.
          </p>
        </CardContent>
      </Card>
    </div>
  {/if}

  <Separator />

  <!-- Clinical Context Footer -->
  <Card class="border bg-muted/30">
    <CardContent class="pt-6">
      <div class="grid grid-cols-1 @3xl:grid-cols-3 gap-6 text-sm">
        <div>
          <h4 class="font-semibold mb-2">About This Report</h4>
          <p class="text-muted-foreground">
            The AGP is a standardized report format recommended by diabetes
            organizations worldwide. It's designed to quickly show patterns that
            help optimize treatment.
          </p>
        </div>
        <div>
          <h4 class="font-semibold mb-2">For Healthcare Providers</h4>
          <p class="text-muted-foreground">
            This AGP follows international consensus guidelines. The modal day
            view with 10th-90th percentile bands helps identify variability
            patterns and timing of excursions.
          </p>
        </div>
        <div>
          <h4 class="font-semibold mb-2">Next Steps</h4>
          <p class="text-muted-foreground">
            Use this report with your care team to identify specific times of
            day that need attention and to track progress over time.
          </p>
        </div>
      </div>
    </CardContent>
  </Card>

  <div class="text-xs text-muted-foreground text-center">
    Data from {startDate.toLocaleDateString()} – {endDate.toLocaleDateString()}.
    {#if lastUpdated}
      Last updated {new Date(lastUpdated).toLocaleString()}.
    {/if}
  </div>
</div>
{/if}

<style>
  /* Expand collapsible clinical detail when printing. */
  @media print {
    details > :not(summary) {
      display: block;
    }
    summary {
      display: none;
    }
  }
</style>
