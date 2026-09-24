<script lang="ts">
  import { formatNumber, formatNumericDate } from "$lib/utils/formatting";
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
    PieChart,
    Calendar,
    Info,
    ArrowRight,
    ArrowLeft,
    HelpCircle,
    Syringe,
    Layers,
  } from "lucide-svelte";
  import BasalBolusRatioChart from "$lib/components/reports/BasalBolusRatioChart.svelte";
  import InsulinDeliveryChart from "$lib/components/reports/InsulinDeliveryChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import type { InsulinDeliveryStatistics } from "$lib/api";
  import {
    getInsulinDeliveryStatistics,
    getDailyBasalBolusRatios,
    getHourlyInsulinDelivery,
  } from "$api/generated/statistics.generated.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 30 days for insulin delivery analysis (TDD and ratios benefit from more data)
  const reportsParams = requireDateParamsContext(30);

  // Date args shared by every statistics query on this page.
  // Send ISO strings, not Date objects. A Date can't be serialised as a
  // remote-query argument ("Unknown date type"), so passing Dates left these
  // queries erroring — empty on first load, hard error when the filter dates
  // change. The server schema is z.coerce.date(), which parses the ISO strings
  // back to dates.
  // Same pattern as ReplayPanel's replay() call.
  const statisticsDates = $derived({
    startDate: reportsParams.startDate.toISOString(),
    endDate: reportsParams.endDate.toISOString(),
  });

  // Routed through contextResource (not a bare $derived query) so a resolved
  // response is retained across superseded query instances. A raw
  // $derived(getDailyBasalBolusRatios(...)) stranded the value on a superseded
  // instance (sveltejs/kit#14915), leaving .current undefined and the chart
  // permanently showing "no insulin data available" even though the endpoint
  // returned full daily data.
  const dailyRatiosResource = contextResource(
    () => getDailyBasalBolusRatios(statisticsDates),
    { errorTitle: "Error Loading Insulin Delivery Data" }
  );

  // Headline insulin figures for the selected range. The fixed-bucket
  // multi-period endpoint was used here instead, so every number above the
  // charts described the last 30 days no matter what the picker said.
  const insulinResource = contextResource(
    () => getInsulinDeliveryStatistics(statisticsDates),
    {
      errorTitle: "Error Loading Insulin Delivery Data",
      dateParams: reportsParams,
    }
  );

  // Hourly delivery pattern with automatic layout registration. Computed
  // backend-side from pump-confirmed records, bucketed by the user's timezone.
  const hourlyDeliveryResource = contextResource(
    () => getHourlyInsulinDelivery(statisticsDates),
    { errorTitle: "Error Loading Insulin Delivery Data" }
  );
  const hourlyDelivery = $derived(hourlyDeliveryResource.current?.hours ?? []);

  const emptyStats: InsulinDeliveryStatistics = {
    totalBolus: 0,
    totalBasal: 0,
    totalInsulin: 0,
    totalCarbs: 0,
    bolusCount: 0,
    basalCount: 0,
    basalPercent: 0,
    bolusPercent: 0,
    tdd: 0,
    avgBolus: 0,
    mealBoluses: 0,
    correctionBoluses: 0,
    icRatio: 0,
    bolusesPerDay: 0,
    carbCount: 0,
    carbBolusCount: 0,
  };

  const insulinStats = $derived(insulinResource.current ?? emptyStats);

  const startDate = $derived(insulinResource.date.from);
  const endDate = $derived(insulinResource.date.to);
  const dayCount = $derived(insulinResource.date.dayCount);
</script>

<svelte:head>
  <title>Insulin Delivery Report - Nocturne Reports</title>
  <meta
    name="description"
    content="Analyze your insulin delivery patterns including basal/bolus ratios and TDD trends"
  />
</svelte:head>

{#if insulinResource.current}
<div class="@container container mx-auto max-w-7xl space-y-8 p-3 @md:p-6">
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4 print:hidden">
      <div>
        <h1 class="flex items-center gap-3 text-2xl font-bold @md:text-3xl">
          <PieChart class="h-7 w-7 text-report-treatment @md:h-8 @md:w-8" />
          Insulin Delivery Report
        </h1>
        <p class="mt-1 text-muted-foreground">
          Comprehensive analysis of your basal and bolus insulin patterns
        </p>
      </div>
      <div class="flex items-center gap-2">
        <Button
          href="/reports/basal-analysis"
          variant="outline"
          size="sm"
        >
          Basal Analysis
          <ArrowRight class="h-4 w-4" />
        </Button>
      </div>
    </div>

    <div class="flex items-center gap-2 text-sm text-muted-foreground print:hidden">
      <Calendar class="h-4 w-4" />
      <span>
        {formatNumericDate(startDate)} – {formatNumericDate(endDate)}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{dayCount} days</span>
      <span class="text-muted-foreground/50">•</span>
      <span>
        {(insulinStats.bolusCount ?? 0) + (insulinStats.basalCount ?? 0)} insulin events
      </span>
    </div>
    <ReliabilityBadge reliability={insulinStats?.reliability} />
  </div>

  <Card variant="info">
    <CardHeader class="pb-3">
      <CardTitle class="flex items-center gap-2 text-base">
        <HelpCircle class="h-5 w-5 text-info" />
        Understanding Basal/Bolus Balance
      </CardTitle>
    </CardHeader>
    <CardContent size="sm" class="space-y-2">
      <p>
        Your <strong>Total Daily Dose (TDD)</strong>
        is split between two types of insulin:
      </p>
      <ul class="list-inside list-disc space-y-1 pl-2 text-muted-foreground">
        <li>
          <strong>Basal insulin:</strong>
          Continuous background insulin that covers your body's baseline needs
        </li>
        <li>
          <strong>Bolus insulin:</strong>
          Insulin taken for meals and to correct high glucose
        </li>
      </ul>
      <p class="text-muted-foreground">
        A typical split is around 50/50, and it varies with diet, activity and
        individual needs.
      </p>
    </CardContent>
  </Card>

  <FigureStrip
    figures={[
      { label: "Avg TDD", value: (insulinStats.tdd ?? 0).toFixed(1), unit: "units/day" },
      { label: "Basal", value: (insulinStats.basalPercent ?? 0).toFixed(0), unit: "%", note: `${(insulinStats.totalBasal ?? 0).toFixed(1)}U total` },
      { label: "Bolus", value: (insulinStats.bolusPercent ?? 0).toFixed(0), unit: "%", note: `${(insulinStats.totalBolus ?? 0).toFixed(1)}U total` },
      { label: "Boluses/Day", value: (insulinStats.bolusesPerDay ?? 0).toFixed(1), note: `avg ${(insulinStats.avgBolus ?? 0).toFixed(1)}U each` },
      { label: "Avg I:C", value: (insulinStats.icRatio ?? 0) > 0 ? `1:${(insulinStats.icRatio ?? 0).toFixed(0)}` : "–", note: `${(insulinStats.totalCarbs ?? 0).toFixed(0)}g carbs` },
    ]}
  />

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <PieChart class="h-5 w-5 text-muted-foreground" />
        Daily Basal/Bolus Breakdown
      </CardTitle>
      <CardDescription>See how your insulin was split each day</CardDescription>
    </CardHeader>
    <CardContent>
      <BasalBolusRatioChart
        data={dailyRatiosResource.current}
        loading={dailyRatiosResource.loading}
      />
    </CardContent>
  </Card>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Syringe class="h-5 w-5 text-muted-foreground" />
        Hourly Insulin Delivery
      </CardTitle>
      <CardDescription>
        Average insulin delivered by hour of day, split by basal and bolus
      </CardDescription>
    </CardHeader>
    <CardContent>
      <InsulinDeliveryChart data={hourlyDelivery} showStacked={true} />
    </CardContent>
  </Card>

  {#if (insulinStats.bolusCount ?? 0) > 0}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Info class="h-5 w-5 text-muted-foreground" />
          Bolus Breakdown
        </CardTitle>
        <CardDescription>
          Understanding your bolus insulin usage
        </CardDescription>
      </CardHeader>
      <CardContent>
        <dl class="m-0 divide-y divide-border">
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 py-2 first:pt-0">
            <dt class="text-sm font-medium text-muted-foreground">Total Boluses</dt>
            <dd class="m-0 flex items-baseline gap-2">
              <span class="text-lg font-semibold tabular-nums">{insulinStats.bolusCount ?? 0}</span>
              <span class="text-xs text-muted-foreground">Over {dayCount} days</span>
            </dd>
          </div>
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 py-2">
            <dt class="text-sm font-medium text-muted-foreground">Meal Boluses</dt>
            <dd class="m-0 flex items-baseline gap-2">
              <span class="text-lg font-semibold tabular-nums">{insulinStats.mealBoluses ?? 0}</span>
              <span class="text-xs text-muted-foreground">
                {(insulinStats.bolusCount ?? 0) > 0
                  ? (
                      ((insulinStats.mealBoluses ?? 0) / (insulinStats.bolusCount ?? 1)) *
                      100
                    ).toFixed(0)
                  : 0}% of boluses
              </span>
            </dd>
          </div>
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 py-2">
            <dt class="text-sm font-medium text-muted-foreground">Correction Boluses</dt>
            <dd class="m-0 flex items-baseline gap-2">
              <span class="text-lg font-semibold tabular-nums">{insulinStats.correctionBoluses ?? 0}</span>
              <span class="text-xs text-muted-foreground">
                {(insulinStats.bolusCount ?? 0) > 0
                  ? (
                      ((insulinStats.correctionBoluses ?? 0) / (insulinStats.bolusCount ?? 1)) *
                      100
                    ).toFixed(0)
                  : 0}% of boluses
              </span>
            </dd>
          </div>
        </dl>

      </CardContent>
    </Card>
  {/if}

  <Card variant="muted">
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <Layers class="h-5 w-5 text-muted-foreground" />
        Clinical Reference
      </CardTitle>
    </CardHeader>
    <CardContent variant="muted" class="space-y-3">
      <p>
        <strong>Total Daily Dose (TDD):</strong>
        Typically ranges from 0.4-1.0 units/kg body weight for Type 1 diabetes. Your
        TDD of
        <strong>{(insulinStats.tdd ?? 0).toFixed(1)}U/day</strong>
        can be compared to this reference.
      </p>
      <p>
        <strong>I:C Ratio Check:</strong>
        Your average insulin-to-carb ratio of 1:{(insulinStats.icRatio ?? 0).toFixed(
          0
        )}
        {#if (insulinStats.icRatio ?? 0) > 0}
          means you use about 1 unit of insulin for every {(insulinStats.icRatio ?? 0).toFixed(
            0
          )} grams of carbs.
        {/if}
      </p>
    </CardContent>
  </Card>

  <Separator class="print:hidden" />
  <div class="flex flex-wrap items-center justify-center gap-2 print:hidden">
    <Button href="/reports" variant="outline" size="sm">
      <ArrowLeft class="h-4 w-4" />
      All Reports
    </Button>
    <Button href="/reports/basal-analysis" size="sm">
      Basal Rate Analysis
      <ArrowRight class="h-4 w-4" />
    </Button>
    <Button href="/reports/treatments" variant="outline" size="sm">
      Treatment Log
    </Button>
  </div>

  <div class="space-y-1 text-center text-xs text-muted-foreground">
    <p class="print:hidden">
      Report generated from {formatNumber(insulinStats.bolusCount)} boluses between
      {formatNumericDate(startDate)} and {formatNumericDate(endDate)}
    </p>
    <p class="text-muted-foreground/60">
      This report is for informational purposes only. Always consult your
      healthcare provider for medical advice.
    </p>
  </div>
</div>
{/if}
