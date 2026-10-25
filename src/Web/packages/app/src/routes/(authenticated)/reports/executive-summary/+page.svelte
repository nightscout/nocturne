<script lang="ts">
  import {
    a1cDisplayValue,
    a1cLabel,
    a1cUnitLabel,
    formatA1cValue,
    formatA1c,
  } from "$lib/utils/a1c-formatting";
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
  import Gauge from "@lucide/svelte/icons/gauge";
  import Target from "@lucide/svelte/icons/target";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import Shield from "@lucide/svelte/icons/shield";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import Activity from "@lucide/svelte/icons/activity";
  import BarChart3 from "@lucide/svelte/icons/chart-column";
  import Calendar from "@lucide/svelte/icons/calendar";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ClinicalInsights from "$lib/components/reports/ClinicalInsights.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import TextureSwatch from "$lib/components/charts/print/TextureSwatch.svelte";
  import { TargetStatus } from "$api/generated/nocturne-api-client";
  import { getReportsData } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg, bgLabel, bgRange, formatMediumDateTime, formatNumber } from "$lib/utils/formatting";
  import { formatMinutesDuration } from "$lib/utils/duration";

  // Format a nullable mg/dL value in the user's preferred units, or em dash if absent.
  const bgOr = (mgdl: number | undefined | null) =>
    mgdl != null ? bg(mgdl) : "–";

  const bgRangeOr = (low: number | undefined | null, high: number | undefined | null) =>
    low != null && high != null ? bgRange(low, high) : "–";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is standard for executive summary reports
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration; `date` carries the
  // selected range so per-day figures divide by the days the user picked.
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error loading executive summary", dateParams: reportsParams }
  );

  const entries = $derived(reportsResource.current?.entries ?? []);
  const analysis = $derived(reportsResource.current?.analysis);
  const lastUpdated = $derived(reportsResource.current?.dateRange?.lastUpdated);
  const dayCount = $derived(reportsResource.date.dayCount);
  const cvStatus = $derived(analysis?.clinicalAssessment?.cvAssessment?.status);
  const cvMeetsTarget = $derived(
    cvStatus == null || cvStatus === TargetStatus.NotAssessed
      ? undefined
      : cvStatus === TargetStatus.Met
  );
</script>

{#snippet targetState(target: string, met: boolean | undefined)}
  <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
    <span class="text-muted-foreground">{target}</span>
    {#if met != null}
      <span class="flex items-center gap-1 font-medium {met ? 'text-success' : 'text-warning'}">
        {#if met}
          <CircleCheck class="w-4 h-4" />
          Target met
        {:else}
          <AlertTriangle class="w-4 h-4" />
          Target not met
        {/if}
      </span>
    {/if}
  </div>
{/snippet}

{#if reportsResource.current}
  <div class="@container space-y-8">
    {#if analysis}
      {@const tir = analysis?.timeInRange?.percentages}
      {@const edges = analysis?.timeInRange?.thresholds}
      {@const dailyMinutes = analysis?.timeInRange?.averageDailyMinutes}
      {@const variability = analysis?.glycemicVariability}
      {@const stats = analysis?.basicStats}
      {@const quality = analysis?.dataQuality}

      <FigureStrip
        figures={[
          { label: "Time in range", value: tir?.target?.toFixed(0) ?? "–", unit: "%", note: "Target: ≥70%" },
          { label: "Below range", value: (tir?.belowRange ?? 0).toFixed(1), unit: "%", note: "Target: <4%" },
          { label: "Above range", value: (tir?.aboveRange ?? 0).toFixed(1), unit: "%", note: "Target: <25%" },
          { label: a1cLabel(true), value: formatA1cValue(variability?.estimatedA1cDisplay), unit: a1cUnitLabel(), note: variability?.a1cTarget ? `Target: <${formatA1c(variability.a1cTarget)}` : undefined, },
          { label: "CV", value: variability?.coefficientOfVariation?.toFixed(0) ?? "–", unit: "%", note: variability?.coefficientOfVariationTarget != null ? `Target: ≤${variability.coefficientOfVariationTarget}%` : undefined },
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
              Time in range
            </CardTitle>
            <CardDescription>
              Percentage of time in the consensus target range ({bgRangeOr(edges?.targetBottom, edges?.targetTop)})
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-6">
            <!-- Stacked Bar Chart -->
            <div class="h-32 w-full overflow-hidden">
              <TIRStackedChart percentages={tir} />
            </div>

            <!-- Duration Breakdown -->
            <div class="space-y-2 text-xs pt-4 border-t">
              <h4 class="font-medium text-sm">Time breakdown (per day avg)</h4>
              <div class="grid grid-cols-3 gap-2">
                <div class="flex flex-col">
                  <span class="text-glucose-in-range font-medium print:text-foreground">In range</span>
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
              {a1cLabel(true)}
            </CardTitle>
          </CardHeader>
          <CardContent class="space-y-4">
            <div>
              {#if variability?.estimatedA1c != null}
                <div class="text-4xl font-semibold tabular-nums">
                  {formatA1c(variability.estimatedA1cDisplay)}
                </div>
                {@render targetState(
                  `Target: below ${formatA1c(variability.a1cTarget)}`,
                  a1cDisplayValue(variability.estimatedA1cMeetsTarget)
                )}
                <p class="text-sm text-muted-foreground mt-1">
                  Your care team sets your individual target.
                </p>
              {:else}
                <div class="text-lg font-medium text-muted-foreground">
                  No estimate for this window
                </div>
                <p class="text-sm text-muted-foreground mt-1">
                  An estimate needs enough readings to compute a mean glucose.
                </p>
              {/if}
              <ReliabilityBadge
                reliability={analysis?.reliability}
                timeZoneUnavailableReason={reportsResource.current?.timeZoneUnavailableReason}
              />
            </div>

            <div class="text-sm space-y-2 border-t pt-3">
              <p>
                <strong>What is {a1cLabel(true)}?</strong>
                This estimates what your lab {a1cLabel()} would be based on your average glucose.
              </p>
              <details class="text-xs">
                <summary class="cursor-pointer text-primary hover:underline">
                  Clinical details
                </summary>
                <p class="mt-2 text-muted-foreground">
                  Uses the ADAG formula (NGSP %). {a1cLabel(true)} = (mean glucose in
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
              Glucose stability
            </CardTitle>
          </CardHeader>
          <CardContent class="space-y-4">
            {#if variability?.coefficientOfVariation == null}
              <div class="text-lg font-medium text-muted-foreground">
                No data for this window
              </div>
            {:else}
              <div class="text-xs text-muted-foreground">Coefficient of variation</div>
              <div class="text-4xl font-semibold tabular-nums">
                {variability.coefficientOfVariation.toFixed(0)}%
              </div>
              {@render targetState(
                `Target: ≤${variability.coefficientOfVariationTarget}%`,
                cvMeetsTarget
              )}
            {/if}

            <p class="text-sm text-muted-foreground">
              Lower means steadier glucose with fewer ups and downs.
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
              Low glucose events
            </CardTitle>
            <CardDescription>
              Time spent below {bgOr(edges?.low)} {bgLabel()} (target: &lt;4%)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div>
              <div class="space-y-1 text-sm tabular-nums">
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="very-low" />
                  <span>&lt;{bgOr(edges?.veryLow)}: {tir?.veryLow?.toFixed(1) ?? 0}%</span>
                </div>
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="low" />
                  <span>{bgOr(edges?.veryLow)}-{bgOr(edges?.low)}: {tir?.low?.toFixed(1) ?? 0}%</span>
                </div>
              </div>
            </div>

            <!-- Episodes count if available -->
            {#if analysis?.timeInRange?.episodes}
              <div class="border-t pt-3 text-sm">
                <div class="flex justify-between">
                  <span class="text-muted-foreground">Low episodes</span>
                  <span class="font-medium tabular-nums">
                    {analysis.timeInRange.episodes.belowRange ?? 0}
                  </span>
                </div>
              </div>
            {/if}

            <div class="text-sm text-muted-foreground">
              Target for time below {bgOr(edges?.low)} {bgLabel()} is under 4%.
            </div>
          </CardContent>
        </Card>

        <!-- Hyperglycemia -->
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              <TrendingUp class="w-5 h-5 text-glucose-high" />
              High glucose events
            </CardTitle>
            <CardDescription>
              Time spent above {bgOr(edges?.high)} {bgLabel()} (target: &lt;25%)
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div>
              <div class="space-y-1 text-sm tabular-nums">
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="high" />
                  <span>{bgOr(edges?.high)}-{bgOr(edges?.veryHigh)}: {tir?.high?.toFixed(1) ?? 0}%</span>
                </div>
                <div class="flex items-center gap-2">
                  <TextureSwatch texture="very-high" />
                  <span>&gt;{bgOr(edges?.veryHigh)}: {tir?.veryHigh?.toFixed(1) ?? 0}%</span>
                </div>
              </div>
            </div>

            <div class="text-sm text-muted-foreground">
              Target for time above {bgOr(edges?.high)} {bgLabel()} is under 25%. The AGP
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
              Glucose statistics
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
              <h4 class="text-sm font-medium mb-3">Glucose distribution</h4>
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
              Data quality
            </CardTitle>
            <CardDescription>How complete is your CGM data?</CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="flex items-center justify-between">
              <span class="text-sm">CGM active time</span>
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
                Limited data can affect report accuracy.
              </div>
            {/if}
            <p class="text-xs text-muted-foreground">
              Target: at least 70% CGM active time over 14 days. The statistics
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
