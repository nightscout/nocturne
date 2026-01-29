<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Progress } from "$lib/components/ui/progress";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Gauge,
    Target,
    TrendingUp,
    Shield,
    AlertTriangle,
    CheckCircle2,
    Info,
    Activity,
    Zap,
    BarChart3,
    Calendar,
    BookOpen,
  } from "lucide-svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ClinicalInsights from "$lib/components/reports/ClinicalInsights.svelte";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { resource } from "runed";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is standard for executive summary reports
  const reportsParams = requireDateParamsContext(14);

  // Use resource for controlled reactivity - prevents excessive re-fetches
  const reportsResource = resource(
    () => reportsParams.dateRangeInput,
    async (dateRangeInput) => {
      return await getReportsData(dateRangeInput);
    },
    { debounce: 100 }
  );

  // Loading state
  const isLoading = $derived(reportsResource.loading);

  const dateRange = $derived(
    reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
      lastUpdated: new Date().toISOString(),
    }
  );
  const entries = $derived(reportsResource.current?.entries ?? []);
  const analysis = $derived(reportsResource.current?.analysis);

  // Helper to get date values
  const startDate = $derived(new Date(dateRange.from));
  const endDate = $derived(new Date(dateRange.to));
  const dayCount = $derived(
    Math.round(
      (endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24)
    )
  );

  // Clinical assessment helpers
  function getOverallAssessment(
    tir: number,
    cv: number,
    lows: number
  ): {
    grade: string;
    label: string;
    description: string;
    color: string;
  } {
    let score = 0;

    // TIR scoring (max 40 points)
    if (tir >= 70) score += 40;
    else if (tir >= 60) score += 30;
    else if (tir >= 50) score += 20;
    else score += 10;

    // CV scoring (max 30 points)
    if (cv <= 33) score += 30;
    else if (cv <= 36) score += 25;
    else if (cv <= 40) score += 15;
    else score += 5;

    // Low avoidance scoring (max 30 points)
    if (lows < 1) score += 30;
    else if (lows < 4) score += 25;
    else if (lows < 6) score += 15;
    else score += 5;

    if (score >= 90)
      return {
        grade: "A",
        label: "Excellent",
        description: "Outstanding glucose management!",
        color: "text-green-600",
      };
    if (score >= 75)
      return {
        grade: "B",
        label: "Good",
        description: "Strong management with room for fine-tuning.",
        color: "text-blue-600",
      };
    if (score >= 60)
      return {
        grade: "C",
        label: "Fair",
        description: "Making progress — let's find areas to improve.",
        color: "text-yellow-600",
      };
    if (score >= 45)
      return {
        grade: "D",
        label: "Needs Attention",
        description: "Some areas need focus. Your care team can help.",
        color: "text-orange-600",
      };
    return {
      grade: "F",
      label: "Critical",
      description: "Please discuss with your healthcare provider soon.",
      color: "text-red-600",
    };
  }

  function formatDuration(minutes: number): string {
    const hours = Math.floor(minutes / 60);
    const mins = Math.round(minutes % 60);
    if (hours === 0) return `${mins}m`;
    if (mins === 0) return `${hours}h`;
    return `${hours}h ${mins}m`;
  }
</script>

<svelte:head>
  <title>Executive Summary - Nocturne Reports</title>
  <meta
    name="description"
    content="High-level overview of your diabetes management metrics"
  />
</svelte:head>

{#if isLoading && !reportsResource.current}
  <ReportsSkeleton />
{:else if reportsResource.error}
  <div class="container mx-auto px-4 py-6 space-y-8 max-w-6xl">
    <Card class="border-2 border-destructive">
      <CardHeader>
        <CardTitle class="flex items-center gap-2 text-destructive">
          <AlertTriangle class="w-5 h-5" />
          Error Loading Executive Summary
        </CardTitle>
      </CardHeader>
      <CardContent>
        <p class="text-destructive-foreground">
          {reportsResource.error instanceof Error ? reportsResource.error.message : String(reportsResource.error)}
        </p>
        <Button
          variant="outline"
          class="mt-4"
          onclick={() => reportsResource.refetch()}
        >
          Try again
        </Button>
      </CardContent>
    </Card>
  </div>
{:else}
  <div class="container mx-auto px-4 py-6 space-y-8 max-w-6xl">
    <!-- Print-Friendly Header -->
    <div class="print:block hidden text-center mb-8">
      <h1 class="text-2xl font-bold">Diabetes Management Report</h1>
      <p class="text-sm text-muted-foreground">
        {startDate.toLocaleDateString()} – {endDate.toLocaleDateString()}
      </p>
    </div>

    {#if analysis}
      {@const tir = analysis?.timeInRange?.percentages}
      {@const durations = analysis?.timeInRange?.durations}
      {@const variability = analysis?.glycemicVariability}
      {@const stats = analysis?.basicStats}
      {@const quality = analysis?.dataQuality}
      {@const totalLows = (tir?.low ?? 0) + (tir?.severeLow ?? 0)}
      {@const totalHighs = (tir?.high ?? 0) + (tir?.severeHigh ?? 0)}
      {@const assessment = getOverallAssessment(
        tir?.target ?? 0,
        variability?.coefficientOfVariation ?? 40,
        totalLows
      )}

      <!-- Overall Grade Card - The Big Picture -->
      <Card
        class="border-2 border-primary/20 bg-linear-to-br from-background to-muted/30"
      >
        <CardContent class="pt-6">
          <div class="flex flex-col md:flex-row items-center gap-6">
            <!-- Grade Circle -->
            <div class="relative">
              <div
                class="w-32 h-32 rounded-full border-8 {assessment.color.replace(
                  'text-',
                  'border-'
                )} flex items-center justify-center bg-background"
              >
                <span class="text-5xl font-bold {assessment.color}">
                  {assessment.grade}
                </span>
              </div>
            </div>

            <!-- Assessment Details -->
            <div class="flex-1 text-center md:text-left space-y-2">
              <div
                class="flex items-center justify-center md:justify-start gap-2"
              >
                <Badge
                  class="{assessment.color
                    .replace('text-', 'bg-')
                    .replace('-600', '-100')} {assessment.color}"
                >
                  {assessment.label}
                </Badge>
                <span class="text-sm text-muted-foreground">
                  Overall Assessment
                </span>
              </div>
              <p class="text-lg">{assessment.description}</p>
              <p class="text-sm text-muted-foreground">
                Based on Time in Range, Glucose Variability, and Hypoglycemia
                Avoidance
              </p>
            </div>

            <!-- Quick Stats -->
            <div class="grid grid-cols-3 gap-4 text-center">
              <div>
                <div
                  class="text-2xl font-bold {(tir?.target ?? 0) >= 70
                    ? 'text-green-600'
                    : (tir?.target ?? 0) >= 50
                      ? 'text-yellow-600'
                      : 'text-orange-600'}"
                >
                  {tir?.target?.toFixed(0) ?? "–"}%
                </div>
                <div class="text-xs text-muted-foreground">TIR</div>
              </div>
              <div>
                <div
                  class="text-2xl font-bold {(variability?.coefficientOfVariation ??
                    40) <= 33
                    ? 'text-green-600'
                    : (variability?.coefficientOfVariation ?? 40) <= 36
                      ? 'text-yellow-600'
                      : 'text-orange-600'}"
                >
                  {variability?.coefficientOfVariation?.toFixed(0) ?? "–"}%
                </div>
                <div class="text-xs text-muted-foreground">CV</div>
              </div>
              <div>
                <div
                  class="text-2xl font-bold {(variability?.estimatedA1c ?? 8) <
                  7
                    ? 'text-green-600'
                    : (variability?.estimatedA1c ?? 8) < 7.5
                      ? 'text-yellow-600'
                      : 'text-orange-600'}"
                >
                  {variability?.estimatedA1c?.toFixed(1) ?? "–"}%
                </div>
                <div class="text-xs text-muted-foreground">eA1C</div>
              </div>
            </div>
          </div>
        </CardContent>
      </Card>

      <!-- Primary Metrics Grid -->
      <div class="grid grid-cols-1 md:grid-cols-3 gap-6">
        <!-- Time in Range - Featured -->
        <Card class="border-2 md:row-span-2">
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <Target class="w-5 h-5 text-green-600" />
              Time in Range
            </CardTitle>
            <CardDescription>
              Percentage of time in your target zone (70-180 mg/dL)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-6">
            <!-- Stacked Bar Chart -->
            <div class="h-32 w-full overflow-hidden">
              <TIRStackedChart percentages={tir} />
            </div>

            <!-- Duration Breakdown -->
            <div class="space-y-2 text-xs pt-4 border-t">
              <h4 class="font-medium text-sm">Time Breakdown (per day avg)</h4>
              <div class="grid grid-cols-3 gap-2">
                <div class="flex flex-col">
                  <span class="text-green-600 font-medium">In Range</span>
                  <span>
                    {formatDuration(
                      (durations?.target ?? 0) / Math.max(1, dayCount)
                    )}
                  </span>
                </div>
                <div class="flex flex-col">
                  <span class="text-red-600 font-medium">Low</span>
                  <span>
                    {formatDuration(
                      ((durations?.low ?? 0) + (durations?.severeLow ?? 0)) /
                        Math.max(1, dayCount)
                    )}
                  </span>
                </div>
                <div class="flex flex-col">
                  <span class="text-orange-500 font-medium">High</span>
                  <span>
                    {formatDuration(
                      ((durations?.high ?? 0) + (durations?.severeHigh ?? 0)) /
                        Math.max(1, dayCount)
                    )}
                  </span>
                </div>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Estimated A1C -->
        <Card class="border-2">
          <CardHeader class="pb-2">
            <CardTitle class="flex items-center gap-2 text-base">
              <Gauge class="w-5 h-5" />
              Estimated A1C
            </CardTitle>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="text-center">
              <div
                class="text-5xl font-bold {(variability?.estimatedA1c ?? 8) < 7
                  ? 'text-green-600'
                  : (variability?.estimatedA1c ?? 8) < 7.5
                    ? 'text-yellow-600'
                    : 'text-orange-600'}"
              >
                {variability?.estimatedA1c?.toFixed(1) ?? "–"}%
              </div>
              <p class="text-sm text-muted-foreground mt-1">
                {#if (variability?.estimatedA1c ?? 8) < 7}
                  Great! Below the 7% target
                {:else if (variability?.estimatedA1c ?? 8) < 7.5}
                  Near target — keep it up!
                {:else}
                  Room for improvement
                {/if}
              </p>
            </div>

            <!-- What this means -->
            <div class="bg-muted/50 rounded-lg p-3 text-sm space-y-2">
              <p>
                <strong>What is eA1C?</strong>
                This estimates what your lab A1C would be based on your average glucose.
              </p>
              <details class="text-xs">
                <summary class="cursor-pointer text-blue-600 hover:underline">
                  Clinical details
                </summary>
                <p class="mt-2 text-muted-foreground">
                  Calculated using the Nathan formula: eA1C = (GMI + 2.59) /
                  1.59. Based on mean glucose of {stats?.mean?.toFixed(0)} mg/dL over
                  {dayCount}
                  days.
                </p>
              </details>
            </div>
          </CardContent>
        </Card>

        <!-- Glucose Variability -->
        <Card class="border-2">
          <CardHeader class="pb-2">
            <CardTitle class="flex items-center gap-2 text-base">
              <TrendingUp class="w-5 h-5" />
              Glucose Stability
            </CardTitle>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="text-center">
              <div
                class="text-5xl font-bold {(variability?.coefficientOfVariation ??
                  40) <= 33
                  ? 'text-green-600'
                  : (variability?.coefficientOfVariation ?? 40) <= 36
                    ? 'text-yellow-600'
                    : 'text-orange-600'}"
              >
                {variability?.coefficientOfVariation?.toFixed(0) ?? "–"}%
              </div>
              <p class="text-sm text-muted-foreground mt-1">
                Coefficient of Variation (CV)
              </p>
            </div>

            <!-- Interpretation -->
            <div class="space-y-2">
              <div class="flex items-center gap-2">
                {#if (variability?.coefficientOfVariation ?? 40) <= 33}
                  <CheckCircle2 class="w-4 h-4 text-green-600" />
                  <span class="text-sm text-green-600 font-medium">
                    Stable — well done!
                  </span>
                {:else if (variability?.coefficientOfVariation ?? 40) <= 36}
                  <Info class="w-4 h-4 text-blue-600" />
                  <span class="text-sm text-blue-600 font-medium">
                    Good stability
                  </span>
                {:else}
                  <AlertTriangle class="w-4 h-4 text-orange-500" />
                  <span class="text-sm text-orange-500 font-medium">
                    Variable — swings present
                  </span>
                {/if}
              </div>
              <p class="text-xs text-muted-foreground">
                Target: ≤33%. Lower means steadier glucose with fewer ups and
                downs.
              </p>
            </div>

            <!-- Additional variability metrics -->
            <div class="grid grid-cols-2 gap-2 text-xs border-t pt-3">
              <div>
                <div class="font-medium">
                  {stats?.standardDeviation?.toFixed(0) ?? "–"} mg/dL
                </div>
                <div class="text-muted-foreground">Std. Deviation</div>
              </div>
              <div>
                <div class="font-medium">
                  {variability?.meanAmplitudeGlycemicExcursions?.toFixed(0) ??
                    "–"} mg/dL
                </div>
                <div class="text-muted-foreground">MAGE</div>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      <!-- Safety Metrics Row -->
      <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
        <!-- Hypoglycemia -->
        <Card
          class="border-2 {totalLows > 4
            ? 'border-red-200 bg-red-50/30 dark:bg-red-950/30'
            : ''}"
        >
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <AlertTriangle
                class="w-5 h-5 {totalLows > 4
                  ? 'text-red-600'
                  : 'text-yellow-600'}"
              />
              Low Blood Sugar Events
            </CardTitle>
            <CardDescription>
              Time spent below 70 mg/dL (target: &lt;4%)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="flex items-center justify-between">
              <div>
                <div
                  class="text-3xl font-bold {totalLows > 4
                    ? 'text-red-600'
                    : 'text-green-600'}"
                >
                  {totalLows.toFixed(1)}%
                </div>
                <p class="text-sm text-muted-foreground">
                  Total time below range
                </p>
              </div>
              <div class="text-right text-sm">
                <div class="flex items-center gap-2">
                  <div class="w-3 h-3 rounded-full bg-red-500"></div>
                  <span>&lt;54: {tir?.severeLow?.toFixed(1) ?? 0}%</span>
                </div>
                <div class="flex items-center gap-2">
                  <div class="w-3 h-3 rounded-full bg-red-300"></div>
                  <span>54-70: {tir?.low?.toFixed(1) ?? 0}%</span>
                </div>
              </div>
            </div>

            <!-- Episodes count if available -->
            {#if analysis?.timeInRange?.episodes}
              <div class="bg-muted/50 rounded p-3 text-sm">
                <div class="flex justify-between">
                  <span>Low episodes:</span>
                  <span class="font-medium">
                    {(analysis.timeInRange.episodes.low ?? 0) +
                      (analysis.timeInRange.episodes.severeLow ?? 0)}
                  </span>
                </div>
              </div>
            {/if}

            {#if totalLows > 4}
              <div
                class="bg-red-100 dark:bg-red-900/30 rounded p-3 text-sm text-red-700 dark:text-red-300"
              >
                <strong>Action needed:</strong>
                You're experiencing more lows than recommended. Discuss with your
                care team about adjusting your treatment.
              </div>
            {:else}
              <div
                class="bg-green-100 dark:bg-green-900/30 rounded p-3 text-sm text-green-700 dark:text-green-300"
              >
                <CheckCircle2 class="w-4 h-4 inline mr-1" />
                Great job keeping lows under control!
              </div>
            {/if}
          </CardContent>
        </Card>

        <!-- Hyperglycemia -->
        <Card
          class="border-2 {totalHighs > 25
            ? 'border-orange-200 bg-orange-50/30 dark:bg-orange-950/30'
            : ''}"
        >
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <TrendingUp
                class="w-5 h-5 {totalHighs > 25
                  ? 'text-orange-600'
                  : 'text-blue-600'}"
              />
              High Blood Sugar Events
            </CardTitle>
            <CardDescription>
              Time spent above 180 mg/dL (target: &lt;25%)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="flex items-center justify-between">
              <div>
                <div
                  class="text-3xl font-bold {totalHighs > 25
                    ? 'text-orange-600'
                    : 'text-green-600'}"
                >
                  {totalHighs.toFixed(1)}%
                </div>
                <p class="text-sm text-muted-foreground">
                  Total time above range
                </p>
              </div>
              <div class="text-right text-sm">
                <div class="flex items-center gap-2">
                  <div class="w-3 h-3 rounded-full bg-orange-400"></div>
                  <span>180-250: {tir?.high?.toFixed(1) ?? 0}%</span>
                </div>
                <div class="flex items-center gap-2">
                  <div class="w-3 h-3 rounded-full bg-orange-600"></div>
                  <span>&gt;250: {tir?.severeHigh?.toFixed(1) ?? 0}%</span>
                </div>
              </div>
            </div>

            {#if totalHighs > 25}
              <div
                class="bg-orange-100 dark:bg-orange-900/30 rounded p-3 text-sm text-orange-700 dark:text-orange-300"
              >
                <strong>Consider:</strong>
                Look at post-meal patterns and correction doses. Your AGP report can
                help identify when highs occur most.
              </div>
            {:else}
              <div
                class="bg-green-100 dark:bg-green-900/30 rounded p-3 text-sm text-green-700 dark:text-green-300"
              >
                <CheckCircle2 class="w-4 h-4 inline mr-1" />
                Time above range is well controlled!
              </div>
            {/if}
          </CardContent>
        </Card>
      </div>

      <!-- Clinical Insights -->
      <ClinicalInsights {analysis} showClinicalNotes={true} maxInsights={3} />

      <!-- Data Quality & Statistics -->
      <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
        <!-- Glucose Statistics -->
        <Card class="border-2">
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <Activity class="w-5 h-5" />
              Glucose Statistics
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div class="grid grid-cols-2 gap-4">
              <div class="space-y-1">
                <div class="text-2xl font-bold">
                  {stats?.mean?.toFixed(0) ?? "–"}
                </div>
                <div class="text-xs text-muted-foreground">Average (mg/dL)</div>
              </div>
              <div class="space-y-1">
                <div class="text-2xl font-bold">
                  {stats?.median?.toFixed(0) ?? "–"}
                </div>
                <div class="text-xs text-muted-foreground">Median (mg/dL)</div>
              </div>
              <div class="space-y-1">
                <div class="text-2xl font-bold">
                  {stats?.min?.toFixed(0) ?? "–"}
                </div>
                <div class="text-xs text-muted-foreground">Lowest (mg/dL)</div>
              </div>
              <div class="space-y-1">
                <div class="text-2xl font-bold">
                  {stats?.max?.toFixed(0) ?? "–"}
                </div>
                <div class="text-xs text-muted-foreground">Highest (mg/dL)</div>
              </div>
            </div>

            <!-- Percentiles -->
            <div class="mt-4 pt-4 border-t">
              <h4 class="text-sm font-medium mb-3">Glucose Distribution</h4>
              <div class="grid grid-cols-4 gap-2 text-xs text-center">
                <div>
                  <div class="font-medium">
                    {stats?.percentiles?.p10?.toFixed(0) ?? "–"}
                  </div>
                  <div class="text-muted-foreground">10th %ile</div>
                </div>
                <div>
                  <div class="font-medium">
                    {stats?.percentiles?.p25?.toFixed(0) ?? "–"}
                  </div>
                  <div class="text-muted-foreground">25th %ile</div>
                </div>
                <div>
                  <div class="font-medium">
                    {stats?.percentiles?.p75?.toFixed(0) ?? "–"}
                  </div>
                  <div class="text-muted-foreground">75th %ile</div>
                </div>
                <div>
                  <div class="font-medium">
                    {stats?.percentiles?.p90?.toFixed(0) ?? "–"}
                  </div>
                  <div class="text-muted-foreground">90th %ile</div>
                </div>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Data Quality -->
        <Card class="border-2">
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <Shield class="w-5 h-5" />
              Data Quality
            </CardTitle>
            <CardDescription>How complete is your CGM data?</CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="flex items-center justify-between">
              <span class="text-sm">CGM Active Time</span>
              <span class="font-bold">
                {quality?.cgmActivePercent?.toFixed(0) ?? "–"}%
              </span>
            </div>
            <Progress
              value={quality?.cgmActivePercent ?? 0}
              max={100}
              class="h-2"
            />

            {#if (quality?.cgmActivePercent ?? 0) >= 90}
              <div
                class="bg-green-100 dark:bg-green-900/30 rounded p-2 text-sm text-green-700 dark:text-green-300"
              >
                <CheckCircle2 class="w-4 h-4 inline mr-1" />
                Excellent data coverage!
              </div>
            {:else if (quality?.cgmActivePercent ?? 0) >= 70}
              <div
                class="bg-yellow-100 dark:bg-yellow-900/30 rounded p-2 text-sm text-yellow-700 dark:text-yellow-300"
              >
                <Info class="w-4 h-4 inline mr-1" />
                Good coverage. For best insights, aim for 90%+
              </div>
            {:else}
              <div
                class="bg-orange-100 dark:bg-orange-900/30 rounded p-2 text-sm text-orange-700 dark:text-orange-300"
              >
                <AlertTriangle class="w-4 h-4 inline mr-1" />
                Limited data may affect report accuracy
              </div>
            {/if}

            <div class="grid grid-cols-2 gap-4 text-sm pt-2 border-t">
              <div>
                <div class="font-medium">{entries.length.toLocaleString()}</div>
                <div class="text-xs text-muted-foreground">Total readings</div>
              </div>
              <div>
                <div class="font-medium">{dayCount}</div>
                <div class="text-xs text-muted-foreground">Days analyzed</div>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      <!-- Navigation to Other Reports -->
      <Card class="border-2 bg-muted/30">
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Zap class="w-5 h-5" />
            Explore More Reports
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="grid grid-cols-2 md:grid-cols-4 gap-3">
            <Button
              href="/reports/agp"
              variant="outline"
              class="h-auto py-4 flex-col gap-2"
            >
              <BarChart3 class="w-5 h-5" />
              <span class="text-xs">AGP Report</span>
            </Button>
            <Button
              href="/reports/readings"
              variant="outline"
              class="h-auto py-4 flex-col gap-2"
            >
              <Calendar class="w-5 h-5" />
              <span class="text-xs">Day-by-Day</span>
            </Button>
            <Button
              href="/reports/treatments"
              variant="outline"
              class="h-auto py-4 flex-col gap-2"
            >
              <Activity class="w-5 h-5" />
              <span class="text-xs">Treatments</span>
            </Button>
            <Button
              href="/reports"
              variant="outline"
              class="h-auto py-4 flex-col gap-2"
            >
              <BookOpen class="w-5 h-5" />
              <span class="text-xs">All Reports</span>
            </Button>
          </div>
        </CardContent>
      </Card>
    {/if}

    <!-- Footer -->
    <div class="text-xs text-muted-foreground text-center space-y-1 print:mt-8">
      <p>
        Report generated: {new Date(dateRange.lastUpdated).toLocaleString()}
      </p>
      <p class="text-muted-foreground/60">
        This report is for informational purposes. Always consult your
        healthcare provider for medical decisions.
      </p>
    </div>
  </div>
{/if}

<style>
  @media print {
    :global(body) {
      font-size: 12px;
    }
  }
</style>
