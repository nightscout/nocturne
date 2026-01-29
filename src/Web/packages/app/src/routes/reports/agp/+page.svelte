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
    AlertTriangle,
    Info,
    Target,
    TrendingUp,
    ArrowRight,
    Printer,
    HelpCircle,
    CheckCircle2,
  } from "lucide-svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { resource } from "runed";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is the standard AGP report period
  const reportsParams = requireDateParamsContext(14);

  // Use resource for controlled reactivity - prevents excessive re-fetches
  const reportsResource = resource(
    () => reportsParams.dateRangeInput,
    async (dateRangeInput) => {
      return await getReportsData(dateRangeInput);
    },
    { debounce: 100 }
  );

  // Unwrap the data from the resource with null safety
  const data = $derived({
    entries: reportsResource.current?.entries ?? [],
    analysis: reportsResource.current?.analysis,
    averagedStats: reportsResource.current?.averagedStats,
    dateRange: reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
      lastUpdated: new Date().toISOString(),
    },
  });

  // Loading state
  const isLoading = $derived(reportsResource.loading);

  // Derived values from data
  const entries = $derived(data.entries);
  const analysis = $derived(data.analysis);
  const dateRange = $derived(data.dateRange);
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
</script>

<svelte:head>
  <title>Ambulatory Glucose Profile - Nocturne Reports</title>
  <meta
    name="description"
    content="Standard AGP report with glucose pattern overlay, percentile bands, and time-in-range analysis"
  />
</svelte:head>

{#if isLoading && !reportsResource.current}
  <ReportsSkeleton />
{:else}
<div class="container mx-auto px-4 py-6 space-y-8 max-w-7xl">
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
      <div class="flex items-center gap-2">
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
            (70-180 mg/dL) is your target range. Time in this zone is your goal!
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
    <div class="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
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
        <div class="text-3xl font-bold">{stats.mean?.toFixed(0) ?? "–"}</div>
        <div class="text-xs text-muted-foreground">Average</div>
        <div class="text-[10px] text-muted-foreground/70">mg/dL</div>
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
          {((tir.low ?? 0) + (tir.severeLow ?? 0)).toFixed(1)}%
        </div>
        <div class="text-xs text-muted-foreground">Below Range</div>
        <div class="text-[10px] text-red-500">Target: &lt;4%</div>
      </Card>
      <Card class="p-4 text-center">
        <div class="text-3xl font-bold text-orange-500">
          {((tir.high ?? 0) + (tir.severeHigh ?? 0)).toFixed(1)}%
        </div>
        <div class="text-xs text-muted-foreground">Above Range</div>
        <div class="text-[10px] text-orange-500">Target: &lt;25%</div>
      </Card>
    </div>

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
      <CardContent class="h-80 md:h-96">
        <AmbulatoryGlucoseProfile averagedStats={data.averagedStats} />
      </CardContent>
    </Card>

    <!-- Time in Range Visual -->
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
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
            Pattern Observations
          </CardTitle>
          <CardDescription>
            What your AGP reveals about your glucose patterns
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <!-- Target Achievement -->
          <div class="flex items-start gap-3 p-3 rounded-lg bg-muted/50">
            {#if (tir.target ?? 0) >= 70}
              <CheckCircle2 class="w-5 h-5 text-green-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-green-600">
                  Excellent Time in Range
                </p>
                <p class="text-sm text-muted-foreground">
                  You're spending {(tir.target ?? 0).toFixed(0)}% of time in
                  target — above the 70% goal!
                </p>
              </div>
            {:else if (tir.target ?? 0) >= 50}
              <Info class="w-5 h-5 text-blue-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-blue-600">Good Progress</p>
                <p class="text-sm text-muted-foreground">
                  Your TIR of {(tir.target ?? 0).toFixed(0)}% shows room for
                  improvement. Each 5% gain matters!
                </p>
              </div>
            {:else}
              <AlertTriangle class="w-5 h-5 text-orange-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-orange-600">Let's Work on This</p>
                <p class="text-sm text-muted-foreground">
                  Your TIR is {(tir.target ?? 0).toFixed(0)}%. Looking at when
                  highs/lows occur can help identify solutions.
                </p>
              </div>
            {/if}
          </div>

          <!-- Variability -->
          <div class="flex items-start gap-3 p-3 rounded-lg bg-muted/50">
            {#if (variability.coefficientOfVariation ?? 50) <= 33}
              <CheckCircle2 class="w-5 h-5 text-green-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-green-600">Stable Glucose</p>
                <p class="text-sm text-muted-foreground">
                  Your CV of {(variability.coefficientOfVariation ?? 0).toFixed(
                    0
                  )}% indicates steady glucose with minimal swings.
                </p>
              </div>
            {:else if (variability.coefficientOfVariation ?? 50) <= 40}
              <Info class="w-5 h-5 text-blue-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-blue-600">Moderate Variability</p>
                <p class="text-sm text-muted-foreground">
                  Some glucose swings present. The AGP bands show where
                  variation occurs.
                </p>
              </div>
            {:else}
              <AlertTriangle class="w-5 h-5 text-orange-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-orange-600">High Variability</p>
                <p class="text-sm text-muted-foreground">
                  Wide percentile bands suggest significant glucose swings.
                  Check the daily view for patterns.
                </p>
              </div>
            {/if}
          </div>

          <!-- Low Risk -->
          {@const totalLows = (tir.low ?? 0) + (tir.severeLow ?? 0)}
          <div class="flex items-start gap-3 p-3 rounded-lg bg-muted/50">
            {#if totalLows < 1}
              <CheckCircle2 class="w-5 h-5 text-green-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-green-600">Minimal Lows</p>
                <p class="text-sm text-muted-foreground">
                  Excellent job avoiding low blood sugars!
                </p>
              </div>
            {:else if totalLows < 4}
              <Info class="w-5 h-5 text-blue-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-blue-600">Low Risk Acceptable</p>
                <p class="text-sm text-muted-foreground">
                  {totalLows.toFixed(1)}% time below range — within acceptable
                  limits.
                </p>
              </div>
            {:else}
              <AlertTriangle class="w-5 h-5 text-red-600 shrink-0 mt-0.5" />
              <div>
                <p class="font-medium text-red-600">Address Lows</p>
                <p class="text-sm text-muted-foreground">
                  {totalLows.toFixed(1)}% time below range. The daily view can
                  help identify when lows occur.
                </p>
              </div>
            {/if}
          </div>
        </CardContent>
      </Card>
    </div>
  {/if}

  <Separator />

  <!-- Clinical Context Footer -->
  <Card class="border bg-muted/30">
    <CardContent class="pt-6">
      <div class="grid grid-cols-1 md:grid-cols-3 gap-6 text-sm">
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
    Last updated {new Date(dateRange.lastUpdated).toLocaleString()}.
  </div>
</div>
{/if}
