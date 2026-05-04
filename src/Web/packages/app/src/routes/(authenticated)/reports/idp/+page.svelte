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
    Syringe,
    Calendar,
    Target,
    Printer,
    Activity,
    Droplets,
  } from "lucide-svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import GlycemicRiskIndexChart from "$lib/components/reports/GlycemicRiskIndexChart.svelte";
  import ScheduledBasalRateChart from "$lib/components/reports/ScheduledBasalRateChart.svelte";
  import HourlyBolusChart from "$lib/components/reports/HourlyBolusChart.svelte";
  import ScheduleFooter from "$lib/components/reports/ScheduleFooter.svelte";
  import { getIdpData } from "$api/idp.remote";
  import { bg, bgLabel } from "$lib/utils/formatting";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is the standard IDP report period
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration
  const reportsResource = contextResource(
    () => getIdpData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading IDP Report" }
  );

  // Unwrap the data from the resource with null safety
  const data = $derived({
    entries: reportsResource.current?.entries ?? [],
    boluses: reportsResource.current?.boluses ?? [],
    carbIntakes: reportsResource.current?.carbIntakes ?? [],
    insulinDeliveryStats: reportsResource.current?.insulinDeliveryStats,
    profileSummary: reportsResource.current?.profileSummary,
    analysis: reportsResource.current?.analysis,
    averagedStats: reportsResource.current?.averagedStats,
    aidSystemMetrics: reportsResource.current?.aidSystemMetrics,
    dateRange: reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
      lastUpdated: new Date().toISOString(),
    },
  });

  // Derived values from data
  const entries = $derived(data.entries);
  const boluses = $derived(data.boluses);
  const insulinStats = $derived(data.insulinDeliveryStats);
  const analysis = $derived(data.analysis);
  const aidMetrics = $derived(data.aidSystemMetrics);
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
  <title>Insulin Dosing Profile - Nocturne Reports</title>
  <meta
    name="description"
    content="Insulin Dosing Profile report with delivery statistics, glucose metrics, basal analysis, and bolus distribution"
  />
</svelte:head>

{#if reportsResource.current}
<div class="@container container mx-auto px-4 py-6 space-y-8 max-w-7xl">
  <!-- Header -->
  <div class="space-y-4">
    <div class="flex items-center justify-between flex-wrap gap-4">
      <div>
        <h1 class="text-3xl font-bold flex items-center gap-3">
          <Syringe class="w-8 h-8 text-primary" />
          Insulin Dosing Profile
        </h1>
        <p class="text-muted-foreground mt-1">
          Comprehensive insulin delivery analysis with glucose context
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

  <!-- Top Row: Insulin Summary + Glucose Metrics -->
  <div class="grid grid-cols-1 @3xl:grid-cols-2 gap-6">
    <!-- Insulin Summary Card -->
    <Card class="border-2">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Syringe class="w-5 h-5 text-blue-600" />
          Insulin Summary
        </CardTitle>
        <CardDescription>
          Daily insulin delivery breakdown
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {@const days = Math.max(insulinStats?.dayCount ?? 1, 1)}
        {@const avgBasal = insulinStats?.totalBasal != null ? insulinStats.totalBasal / days : null}
        {@const avgBolus = insulinStats?.totalBolus != null ? insulinStats.totalBolus / days : null}
        {@const avgScheduled = insulinStats?.scheduledBasal != null ? insulinStats.scheduledBasal / days : null}

        <!-- TDD -->
        <div class="flex items-baseline justify-between">
          <span class="text-sm text-muted-foreground">Avg Total Daily Dose</span>
          <span class="text-2xl font-bold">
            {insulinStats?.tdd?.toFixed(1) ?? "--"} U/day
          </span>
        </div>

        <!-- Basal / Bolus Split Bar -->
        {@const basalPct = insulinStats?.basalPercent ?? 0}
        {@const bolusPct = insulinStats?.bolusPercent ?? 0}
        <div class="space-y-1">
          <div class="flex justify-between text-xs text-muted-foreground">
            <span>Basal: {avgBasal?.toFixed(1) ?? "--"} U/day ({basalPct.toFixed(0)}%)</span>
            <span>Bolus: {avgBolus?.toFixed(1) ?? "--"} U/day ({bolusPct.toFixed(0)}%)</span>
          </div>
          <div class="flex h-4 rounded-full overflow-hidden">
            <div
              class="transition-all"
              style="width: {basalPct}%; background-color: var(--insulin-scheduled-basal)"
            ></div>
            <div
              class="transition-all"
              style="width: {bolusPct}%; background-color: var(--insulin-bolus)"
            ></div>
          </div>
        </div>

        <Separator />

        <!-- Delivered vs Scheduled (daily avg) -->
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

        <!-- Bolus breakdown (daily avg) -->
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
              {insulinStats?.mealBoluses != null ? (insulinStats.mealBoluses / days).toFixed(1) : "--"}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Correction Boluses/Day</div>
            <div class="font-semibold">
              {insulinStats?.correctionBoluses != null ? (insulinStats.correctionBoluses / days).toFixed(1) : "--"}
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Glucose Metrics Card -->
    <Card class="border-2">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Droplets class="w-5 h-5 text-green-600" />
          Glucose Metrics
        </CardTitle>
        <CardDescription>
          Key glucose indicators for this period
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {#if analysis}
          {@const stats = analysis.basicStats ?? {}}
          {@const variability = analysis.glycemicVariability ?? {}}
          {@const tir = analysis.timeInRange?.percentages ?? {}}

          <!-- Average, GMI, CV -->
          <div class="grid grid-cols-3 gap-4 text-center">
            <div>
              <div class="text-2xl font-bold">{stats.mean ? bg(stats.mean) : "--"}</div>
              <div class="text-xs text-muted-foreground">Average</div>
              <div class="text-[10px] text-muted-foreground/70">{bgLabel()}</div>
            </div>
            <div>
              <div class="text-2xl font-bold">{variability.estimatedA1c?.toFixed(1) ?? "--"}%</div>
              <div class="text-xs text-muted-foreground">GMI</div>
              <div class="text-[10px] text-muted-foreground/70">Est. A1C</div>
            </div>
            <div>
              <div class="text-2xl font-bold">{variability.coefficientOfVariation?.toFixed(0) ?? "--"}%</div>
              <div class="text-xs text-muted-foreground">CV</div>
              <div class="text-[10px] {(variability.coefficientOfVariation ?? 50) <= 33 ? 'text-green-600' : 'text-orange-600'}">
                Target: ≤33%
              </div>
            </div>
          </div>

          <Separator />

          <!-- TIR Horizontal Stacked Bar -->
          <div class="space-y-2">
            <div class="text-sm font-medium">Time in Range</div>
            <div class="h-32">
              <TIRStackedChart percentages={tir} orientation="horizontal" />
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

  {#if analysis?.reliability}
    <ReliabilityBadge reliability={analysis.reliability} />
  {/if}

  <!-- Middle Row: AID Use (stub) + GRI -->
  <div class="grid grid-cols-1 @3xl:grid-cols-2 gap-6">
    <!-- AID Use Card (stubbed) -->
    <Card class="border-2">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Activity class="w-5 h-5 text-purple-600" />
          AID System Use
        </CardTitle>
        <CardDescription>
          Automated insulin delivery system metrics
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="grid grid-cols-2 gap-4 text-sm">
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

    <!-- GRI Card -->
    <Card class="border-2">
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Target class="w-5 h-5 text-red-600" />
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

  <!-- Glucose Pattern — unified section matching iCoDE-2 layout -->
  <Card class="border-2">
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
      <!-- AGP -->
      <div class="h-80 @2xl:h-96">
        <AmbulatoryGlucoseProfile averagedStats={data.averagedStats} />
      </div>

      <!-- Scheduled Basal Rate -->
      <div>
        <h4 class="text-sm font-semibold text-muted-foreground mb-1">Scheduled Basal Rate</h4>
        <div class="h-24">
          <ScheduledBasalRateChart entries={data.profileSummary?.basalSchedules?.[0]?.entries ?? []} />
        </div>
      </div>

      <!-- User-Initiated Boluses Per Day -->
      <div>
        <h4 class="text-sm font-semibold text-muted-foreground mb-1">User-Initiated Boluses Per Day</h4>
        <HourlyBolusChart {boluses} {dayCount} />
      </div>

      <!-- Schedule Bands -->
      <ScheduleFooter profile={data.profileSummary} />
    </CardContent>
  </Card>

  <div class="text-xs text-muted-foreground text-center">
    Data from {startDate.toLocaleDateString()} – {endDate.toLocaleDateString()}.
    Last updated {new Date(dateRange.lastUpdated).toLocaleString()}.
  </div>
</div>
{/if}
