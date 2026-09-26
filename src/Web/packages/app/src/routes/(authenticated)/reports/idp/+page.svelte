<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Syringe,
    Calendar,
    Target,
    Activity,
    Droplets,
  } from "lucide-svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import GlycemicRiskIndexChart from "$lib/components/reports/GlycemicRiskIndexChart.svelte";
  import ScheduledBasalRateChart from "$lib/components/reports/ScheduledBasalRateChart.svelte";
  import HourlyBolusChart from "$lib/components/reports/HourlyBolusChart.svelte";
  import ScheduleFooter from "$lib/components/reports/ScheduleFooter.svelte";
  import TextureSwatch from "$lib/components/charts/print/TextureSwatch.svelte";
  import { bgPatternClass } from "$lib/components/charts/print/chart-print-patterns";
  import { getIdpData } from "$api/idp.remote";
  import { bg, bgLabel, formatMediumDateTime, formatNumber, formatNumericDate } from "$lib/utils/formatting";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is the standard IDP report period
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration; `date` carries the
  // selected range, so the page has one day count rather than two.
  const reportsResource = contextResource(
    () => getIdpData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading IDP Report", dateParams: reportsParams }
  );

  const data = $derived({
    entries: reportsResource.current?.entries ?? [],
    boluses: reportsResource.current?.boluses ?? [],
    insulinDeliveryStats: reportsResource.current?.insulinDeliveryStats,
    profileSummary: reportsResource.current?.profileSummary,
    analysis: reportsResource.current?.analysis,
    averagedStats: reportsResource.current?.averagedStats,
    aidSystemMetrics: reportsResource.current?.aidSystemMetrics,
  });

  // Derived values from data
  const entries = $derived(data.entries);
  const boluses = $derived(data.boluses);
  const insulinStats = $derived(data.insulinDeliveryStats);
  const analysis = $derived(data.analysis);
  const aidMetrics = $derived(data.aidSystemMetrics);
  const stats = $derived(analysis?.basicStats ?? {});
  const variability = $derived(analysis?.glycemicVariability ?? {});
  const lastUpdated = $derived(reportsResource.current?.dateRange?.lastUpdated);
  const startDate = $derived(reportsResource.date.from);
  const endDate = $derived(reportsResource.date.to);
  const dayCount = $derived(reportsResource.date.dayCount);
</script>

<svelte:head>
  <title>Insulin Dosing Profile - Nocturne Reports</title>
  <meta
    name="description"
    content="Insulin Dosing Profile report with delivery statistics, glucose metrics, basal analysis, and bolus distribution"
  />
</svelte:head>

{#if reportsResource.current}
<div class="@container container mx-auto space-y-8 p-3 @md:p-6 max-w-7xl">
  <div class="space-y-4">
    <div class="print:hidden">
      <h1 class="text-2xl @md:text-3xl font-bold flex items-center gap-3">
        <Syringe class="w-6 h-6 @md:w-8 @md:h-8 text-primary" />
        Insulin Dosing Profile
      </h1>
      <p class="text-muted-foreground mt-1">
        Comprehensive insulin delivery analysis with glucose context
      </p>
    </div>

    <div class="flex items-center gap-2 text-sm text-muted-foreground">
      <Calendar class="w-4 h-4 print:hidden" />
      <span class="print:hidden">
        {formatNumericDate(startDate)} – {formatNumericDate(endDate)}
      </span>
      <span class="text-muted-foreground/50 print:hidden">•</span>
      <span class="print:hidden">{dayCount} days</span>
      <span class="text-muted-foreground/50 print:hidden">•</span>
      <span>{formatNumber(entries.length)} readings</span>
    </div>
  </div>

  <FigureStrip
    figures={[
      { label: "Avg Total Daily Dose", value: insulinStats?.tdd?.toFixed(1) ?? "--", unit: "U/day" },
      { label: "Average", value: stats.mean ? String(bg(stats.mean)) : "--", unit: bgLabel() },
      { label: "Est. A1C", value: variability.estimatedA1c?.toFixed(1) ?? "--", unit: "%", note: "From mean glucose" },
      { label: "CV", value: variability.coefficientOfVariation?.toFixed(0) ?? "--", unit: "%", note: "Target: ≤33%" },
    ]}
  />

  {#if analysis?.reliability}
    <ReliabilityBadge reliability={analysis.reliability} />
  {/if}

  <div class="grid grid-cols-1 @3xl:grid-cols-2 print:grid-cols-2 gap-6">
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Syringe class="w-5 h-5 text-insulin" />
          Insulin Summary
        </CardTitle>
        <CardDescription>
          Daily insulin delivery breakdown
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {@const avgBasal = insulinStats?.totalBasal != null ? insulinStats.totalBasal / dayCount : null}
        {@const avgBolus = insulinStats?.totalBolus != null ? insulinStats.totalBolus / dayCount : null}
        {@const avgScheduled = insulinStats?.scheduledBasal != null ? insulinStats.scheduledBasal / dayCount : null}

        {@const basalPct = insulinStats?.basalPercent ?? 0}
        {@const bolusPct = insulinStats?.bolusPercent ?? 0}
        <div class="space-y-1">
          <div class="flex justify-between text-xs text-muted-foreground">
            <span class="flex items-center gap-1">
              <TextureSwatch texture="insulin-scheduled-basal" color="var(--basal)" />
              Basal: {avgBasal?.toFixed(1) ?? "--"} U/day ({basalPct.toFixed(0)}%)
            </span>
            <span class="flex items-center gap-1">
              <TextureSwatch texture="insulin-bolus" />
              Bolus: {avgBolus?.toFixed(1) ?? "--"} U/day ({bolusPct.toFixed(0)}%)
            </span>
          </div>
          <div class="flex h-4 rounded-full overflow-hidden">
            <div
              class="w-(--share) bg-basal {bgPatternClass('insulin-scheduled-basal')} transition-all"
              style:--share="{basalPct}%"
            ></div>
            <div
              class="w-(--share) bg-insulin-bolus {bgPatternClass('insulin-bolus')} transition-all"
              style:--share="{bolusPct}%"
            ></div>
          </div>
        </div>

        <Separator />

        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <div class="text-muted-foreground">Avg Delivered Basal</div>
            <div class="font-semibold">{avgBasal?.toFixed(1) ?? "--"} U/day</div>
          </div>
          <div>
            <div class="text-muted-foreground">Avg Scheduled Basal</div>
            <div class="font-semibold">{avgScheduled?.toFixed(1) ?? "--"} U/day</div>
          </div>
        </div>

        <Separator />

        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <div class="text-muted-foreground">Boluses/Day</div>
            <div class="font-semibold">{insulinStats?.bolusesPerDay?.toFixed(1) ?? "--"}</div>
          </div>
          <div>
            <div class="text-muted-foreground">Avg Bolus Size</div>
            <div class="font-semibold">{insulinStats?.avgBolus?.toFixed(1) ?? "--"} U</div>
          </div>
          <div>
            <div class="text-muted-foreground">Meal Boluses/Day</div>
            <div class="font-semibold">
              {insulinStats?.mealBoluses != null ? (insulinStats.mealBoluses / dayCount).toFixed(1) : "--"}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Correction Boluses/Day</div>
            <div class="font-semibold">
              {insulinStats?.correctionBoluses != null ? (insulinStats.correctionBoluses / dayCount).toFixed(1) : "--"}
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Droplets class="w-5 h-5 text-glucose-in-range" />
          Glucose Metrics
        </CardTitle>
        <CardDescription>
          Key glucose indicators for this period
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {#if analysis}
          {@const tir = analysis.timeInRange?.percentages ?? {}}
          <div class="space-y-2">
            <div class="text-sm font-medium">Time in Range</div>
            <div class="h-56 w-full">
              <TIRStackedChart percentages={tir} />
            </div>
          </div>
        {:else}
          <div class="text-center text-muted-foreground py-8">
            No glucose analysis available
          </div>
        {/if}
      </CardContent>
    </Card>
  </div>

  <div class="grid grid-cols-1 @3xl:grid-cols-2 gap-6">
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Activity class="w-5 h-5" />
          AID System Use
        </CardTitle>
        <CardDescription>
          Automated insulin delivery system metrics
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="grid grid-cols-1 @xs:grid-cols-2 print:grid-cols-3 gap-4 text-sm">
          <div>
            <div class="text-muted-foreground">CGM</div>
            <div class="font-semibold text-lg">{aidMetrics?.cgmDeviceNames ?? '--'}</div>
          </div>
          <div>
            <div class="text-muted-foreground">Pump</div>
            <div class="font-semibold text-lg">{aidMetrics?.pumpDeviceNames ?? '--'}</div>
          </div>
          <div>
            <div class="text-muted-foreground">CGM Active</div>
            <div class="font-semibold text-lg">{aidMetrics?.cgmActivePercent != null ? `${Math.round(aidMetrics.cgmActivePercent)}%` : '--'}</div>
          </div>
          <div>
            <div class="text-muted-foreground">AID Active</div>
            <div class="font-semibold text-lg">{aidMetrics?.aidActivePercent != null ? `${Math.round(aidMetrics.aidActivePercent)}%` : '--'}</div>
          </div>
          <div>
            <div class="text-muted-foreground">Target</div>
            <div class="font-semibold text-lg">{aidMetrics?.targetLow != null && aidMetrics?.targetHigh != null ? `${bg(aidMetrics.targetLow)}-${bg(aidMetrics.targetHigh)} ${bgLabel()}` : '--'}</div>
          </div>
          <div>
            <div class="text-muted-foreground">Site Changes</div>
            <div class="font-semibold text-lg">{aidMetrics?.siteChangeCount != null ? aidMetrics.siteChangeCount : '--'}</div>
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Target class="w-5 h-5" />
          Glycemic Risk Index
        </CardTitle>
        <CardDescription>
          Composite metric of hypo and hyperglycemia risk
        </CardDescription>
      </CardHeader>
      <CardContent>
        {#if analysis?.gri}
          <GlycemicRiskIndexChart gri={analysis.gri} />
        {:else}
          <div class="flex items-center justify-center h-full text-muted-foreground">
            No GRI data available
          </div>
        {/if}
      </CardContent>
    </Card>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Activity class="w-5 h-5" />
        Glucose Pattern
      </CardTitle>
      <CardDescription>
        Daily glucose overlay with basal rate, bolus distribution, and dosing profile
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-6">
      <div class="h-80 w-full @2xl:h-96">
        <AmbulatoryGlucoseProfile averagedStats={data.averagedStats} />
      </div>

      <div>
        <h4 class="text-sm font-semibold text-muted-foreground mb-1">Scheduled Basal Rate</h4>
        <div class="h-24 w-full">
          <ScheduledBasalRateChart entries={data.profileSummary?.basalSchedules?.[0]?.entries ?? []} />
        </div>
      </div>

      <div class="w-full">
        <h4 class="text-sm font-semibold text-muted-foreground mb-1">User-Initiated Boluses Per Day</h4>
        <HourlyBolusChart {boluses} {dayCount} />
      </div>

      <ScheduleFooter profile={data.profileSummary} />
    </CardContent>
  </Card>

  <div class="text-xs text-muted-foreground text-center print:hidden">
    Data from {formatNumericDate(startDate)} – {formatNumericDate(endDate)}.
    {#if lastUpdated}
      Last updated {formatMediumDateTime(new Date(lastUpdated))}.
    {/if}
  </div>
</div>
{/if}
