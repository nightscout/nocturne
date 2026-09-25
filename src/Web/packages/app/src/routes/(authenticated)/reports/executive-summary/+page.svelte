<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import { Progress } from "$lib/components/ui/progress";
  import {
    Gauge,
    Target,
    TrendingUp,
    Shield,
    AlertTriangle,
    Activity,
    BarChart3,
    Calendar,
    BookOpen,
  } from "lucide-svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ClinicalInsights from "$lib/components/reports/ClinicalInsights.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import TextureSwatch from "$lib/components/charts/print/TextureSwatch.svelte";
  import { getReportsData } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg, bgLabel, bgRange, formatMediumDateTime, formatNumber } from "$lib/utils/formatting";
  import { formatMinutesDuration } from "$lib/utils/duration";

  // Format a nullable mg/dL value in the user's preferred units, or em dash if absent.
  const bgOr = (mgdl: number | undefined | null) =>
    mgdl != null ? bg(mgdl) : "–";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is standard for executive summary reports
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration; `date` carries the
  // selected range so per-day figures divide by the days the user picked.
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Executive Summary", dateParams: reportsParams }
  );

  const entries = $derived(reportsResource.current?.entries ?? []);
  const analysis = $derived(reportsResource.current?.analysis);
  const lastUpdated = $derived(reportsResource.current?.dateRange?.lastUpdated);
  const dayCount = $derived(reportsResource.date.dayCount);
</script>

<svelte:head>
  <title>Executive Summary - Nocturne Reports</title>
  <meta
    name="description"
    content="High-level overview of your diabetes management metrics"
  />
</svelte:head>

{#if reportsResource.current}
  <div class="@container container mx-auto space-y-8 p-3 @md:p-6 max-w-6xl">
    {#if analysis}
      {@const tir = analysis?.timeInRange?.percentages}
      {@const dailyMinutes = analysis?.timeInRange?.averageDailyMinutes}
      {@const variability = analysis?.glycemicVariability}
      {@const stats = analysis?.basicStats}
      {@const quality = analysis?.dataQuality}
      {@const totalLows = (tir?.low ?? 0) + (tir?.veryLow ?? 0)}
      {@const totalHighs = (tir?.high ?? 0) + (tir?.veryHigh ?? 0)}

      <FigureStrip
        figures={[
          { label: "Time in range", value: tir?.target?.toFixed(0) ?? "–", unit: "%", note: "Target: ≥70%" },
          { label: "Below range", value: totalLows.toFixed(1), unit: "%", note: "Target: <4%" },
          { label: "Above range", value: totalHighs.toFixed(1), unit: "%", note: "Target: <25%" },
          { label: "Est. A1C", value: variability?.estimatedA1c?.toFixed(1) ?? "–", unit: "%", note: "Target: <7%" },
          { label: "CV", value: variability?.coefficientOfVariation?.toFixed(0) ?? "–", unit: "%", note: "Target: ≤33%" },
          { label: "Average", value: String(bgOr(stats?.mean)), unit: bgLabel(), note: `Over ${dayCount} days` },
        ]}
      />

      <!-- Primary Metrics Grid -->
      <div class="grid grid-cols-1 @2xl:grid-cols-2 @4xl:grid-cols-3 gap-6">
        <!-- Time in Range - Featured -->
        <Card class="@2xl:col-span-2 @4xl:col-span-1 @4xl:row-span-2">
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <Target class="w-5 h-5 text-glucose-in-range" />
              Time in Range
            </CardTitle>
            <CardDescription>
              Percentage of time in your target zone ({bgRange(70, 180)})
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
                  <span class="text-glucose-in-range font-medium print:text-foreground">In Range</span>
                  <span>
                    {formatMinutesDuration(dailyMinutes?.target ?? 0)}
                  </span>
                </div>
                <div class="flex flex-col">
                  <span class="text-glucose-very-low font-medium print:text-foreground">Low</span>
                  <span>
                    {formatMinutesDuration(dailyMinutes?.low ?? 0)}
                  </span>
                </div>
                <div class="flex flex-col">
                  <span class="text-glucose-high font-medium print:text-foreground">High</span>
                  <span>
                    {formatMinutesDuration(dailyMinutes?.high ?? 0)}
                  </span>
                </div>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Estimated A1C -->
        <Card>
          <CardHeader class="pb-2">
            <CardTitle class="flex items-center gap-2 text-base">
              <Gauge class="w-5 h-5" />
              Estimated A1C
            </CardTitle>
          </CardHeader>
          <CardContent class="space-y-4">
            <div>
              {#if variability?.estimatedA1c != null}
                <p class="text-sm text-muted-foreground">
                  Target: below 7%. Your care team sets your individual target.
                </p>
              {:else}
                <div class="text-lg font-medium text-muted-foreground">
                  No estimate for this window
                </div>
                <p class="text-sm text-muted-foreground mt-1">
                  An estimate needs enough readings to compute a mean glucose.
                </p>
              {/if}
              <ReliabilityBadge reliability={analysis?.reliability} />
            </div>

            <div class="text-sm space-y-2 border-t pt-3">
              <p>
                <strong>What is eA1C?</strong>
                This estimates what your lab A1C would be based on your average glucose.
              </p>
              <details class="text-xs">
                <summary class="cursor-pointer text-primary hover:underline">
                  Clinical details
                </summary>
                <p class="mt-2 text-muted-foreground">
                  Calculated using the ADAG formula: eA1C = (mean glucose in
                  mmol/L + 2.59) / 1.59. Based on mean glucose of {bgOr(stats?.mean)} {bgLabel()} over
                  {dayCount}
                  days.
                </p>
              </details>
            </div>
          </CardContent>
        </Card>

        <!-- Glucose Variability -->
        <Card>
          <CardHeader class="pb-2">
            <CardTitle class="flex items-center gap-2 text-base">
              <TrendingUp class="w-5 h-5" />
              Glucose Stability
            </CardTitle>
          </CardHeader>
          <CardContent class="space-y-4">
            {#if variability?.coefficientOfVariation == null}
              <div class="text-lg font-medium text-muted-foreground">
                No data for this window
              </div>
            {/if}

            <p class="text-sm text-muted-foreground">
              Target: ≤33%. Lower means steadier glucose with fewer ups and
              downs.
            </p>

            <!-- Additional variability metrics -->
            <dl class="m-0 divide-y divide-border border-t text-sm">
              <div class="flex justify-between py-2">
                <dt class="text-muted-foreground">Standard deviation</dt>
                <dd class="m-0 font-medium tabular-nums">{bgOr(stats?.standardDeviation)} {bgLabel()}</dd>
              </div>
              <div class="flex justify-between py-2">
                <dt class="text-muted-foreground">MAGE</dt>
                <dd class="m-0 font-medium tabular-nums">{bgOr(variability?.meanAmplitudeGlycemicExcursions)} {bgLabel()}</dd>
              </div>
            </dl>
          </CardContent>
        </Card>
      </div>

      <!-- Safety Metrics Row -->
      <div class="grid grid-cols-1 @3xl:grid-cols-2 print:grid-cols-2 gap-6">
        <!-- Hypoglycemia -->
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <AlertTriangle class="w-5 h-5 text-glucose-very-low" />
              Low Blood Sugar Events
            </CardTitle>
            <CardDescription>
              Time spent below {bg(70)} {bgLabel()} (target: &lt;4%)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div>
              <div class="space-y-1 text-sm tabular-nums">
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="very-low" />
                  <span>&lt;{bg(54)}: {tir?.veryLow?.toFixed(1) ?? 0}%</span>
                </div>
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="low" />
                  <span>{bg(54)}-{bg(70)}: {tir?.low?.toFixed(1) ?? 0}%</span>
                </div>
              </div>
            </div>

            <!-- Episodes count if available -->
            {#if analysis?.timeInRange?.episodes}
              <div class="border-t pt-3 text-sm">
                <div class="flex justify-between">
                  <span class="text-muted-foreground">Low episodes</span>
                  <span class="font-medium tabular-nums">
                    {(analysis.timeInRange.episodes.low ?? 0) +
                      (analysis.timeInRange.episodes.veryLow ?? 0)}
                  </span>
                </div>
              </div>
            {/if}

            <div class="text-sm text-muted-foreground">
              Target for time below {bg(70)} {bgLabel()} is under 4%.
            </div>
          </CardContent>
        </Card>

        <!-- Hyperglycemia -->
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <TrendingUp class="w-5 h-5 text-glucose-high" />
              High Blood Sugar Events
            </CardTitle>
            <CardDescription>
              Time spent above {bg(180)} {bgLabel()} (target: &lt;25%)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div>
              <div class="space-y-1 text-sm tabular-nums">
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="high" />
                  <span>{bg(180)}-{bg(250)}: {tir?.high?.toFixed(1) ?? 0}%</span>
                </div>
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="very-high" />
                  <span>&gt;{bg(250)}: {tir?.veryHigh?.toFixed(1) ?? 0}%</span>
                </div>
              </div>
            </div>

            <div class="text-sm text-muted-foreground">
              Target for time above {bg(180)} {bgLabel()} is under 25%. The AGP
              report shows the times of day when highs occur most.
            </div>
          </CardContent>
        </Card>
      </div>

      <!-- Clinical Insights -->
      <ClinicalInsights {analysis} showClinicalNotes={true} maxInsights={3} />

      <!-- Data Quality & Statistics -->
      <div class="grid grid-cols-1 @3xl:grid-cols-2 print:grid-cols-2 gap-6">
        <!-- Glucose Statistics -->
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <Activity class="w-5 h-5" />
              Glucose Statistics
            </CardTitle>
          </CardHeader>
          <CardContent>
            <dl class="m-0 divide-y divide-border text-sm">
              {#each [
                { label: "Average", value: stats?.mean },
                { label: "Median", value: stats?.median },
                { label: "Lowest", value: stats?.min },
                { label: "Highest", value: stats?.max },
              ] as { label, value } (label)}
                <div class="flex items-baseline justify-between py-2">
                  <dt class="text-muted-foreground">{label}</dt>
                  <dd class="m-0 font-semibold tabular-nums">
                    {bgOr(value)}
                    <span class="text-xs font-normal text-muted-foreground">{bgLabel()}</span>
                  </dd>
                </div>
              {/each}
            </dl>

            <!-- Percentiles -->
            <div class="mt-4 pt-4 border-t">
              <h4 class="text-sm font-medium mb-3">Glucose Distribution</h4>
              <div class="grid grid-cols-4 gap-2 text-xs text-center">
                <div>
                  <div class="font-medium">
                    {bgOr(stats?.percentiles?.p10)}
                  </div>
                  <div class="text-muted-foreground">10th %ile</div>
                </div>
                <div>
                  <div class="font-medium">
                    {bgOr(stats?.percentiles?.p25)}
                  </div>
                  <div class="text-muted-foreground">25th %ile</div>
                </div>
                <div>
                  <div class="font-medium">
                    {bgOr(stats?.percentiles?.p75)}
                  </div>
                  <div class="text-muted-foreground">75th %ile</div>
                </div>
                <div>
                  <div class="font-medium">
                    {bgOr(stats?.percentiles?.p90)}
                  </div>
                  <div class="text-muted-foreground">90th %ile</div>
                </div>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Data Quality -->
        <Card>
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

            {#if (quality?.cgmActivePercent ?? 0) < 70}
              <div class="text-sm text-muted-foreground">
                <AlertTriangle class="w-4 h-4 inline mr-1 text-warning" />
                Limited data may affect report accuracy.
              </div>
            {/if}
            <p class="text-xs text-muted-foreground">
              Target: at least 70% CGM active time over 14 days; the statistics
              on this page are most reliable at 90% or above.
            </p>

            <div class="grid grid-cols-2 gap-4 text-sm pt-2 border-t">
              <div>
                <div class="font-medium">{formatNumber(entries.length)}</div>
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

      <nav class="flex flex-wrap gap-2 print:hidden" aria-label="More reports">
        <Button href="/reports/agp" variant="outline" size="sm"><BarChart3 />AGP Report</Button>
        <Button href="/reports/readings" variant="outline" size="sm"><Calendar />Day-by-Day</Button>
        <Button href="/reports/treatments" variant="outline" size="sm"><Activity />Treatments</Button>
        <Button href="/reports" variant="ghost" size="sm"><BookOpen />All reports</Button>
      </nav>
    {/if}

    <!-- Footer -->
    <div class="text-xs text-muted-foreground text-center space-y-1 print:mt-8">
      {#if lastUpdated}
        <p class="print:hidden">Report generated: {formatMediumDateTime(new Date(lastUpdated))}</p>
      {/if}
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
