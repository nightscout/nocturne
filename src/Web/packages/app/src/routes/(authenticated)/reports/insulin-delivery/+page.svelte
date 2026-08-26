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
    PieChart,
    Calendar,
    Info,
    TrendingUp,
    ArrowRight,
    Printer,
    HelpCircle,
    Syringe,
    Layers,
    Target,
  } from "lucide-svelte";
  import BasalBolusRatioChart from "$lib/components/reports/BasalBolusRatioChart.svelte";
  import InsulinDeliveryChart from "$lib/components/reports/InsulinDeliveryChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
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
  // back to dates; the cast satisfies the generated Date arg type.
  // Same pattern as ReplayPanel's replay() call.
  const statisticsDates = $derived({
    startDate: reportsParams.startDate.toISOString() as unknown as Date,
    endDate: reportsParams.endDate.toISOString() as unknown as Date,
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
  <!-- Header -->
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 class="flex items-center gap-3 text-2xl font-bold @md:text-3xl">
          <PieChart class="h-7 w-7 text-blue-600 @md:h-8 @md:w-8" />
          Insulin Delivery Report
        </h1>
        <p class="mt-1 text-muted-foreground">
          Comprehensive analysis of your basal and bolus insulin patterns
        </p>
      </div>
      <div class="flex items-center gap-2 print:hidden">
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
          href="/reports/basal-analysis"
          variant="outline"
          size="sm"
          class="gap-2"
        >
          Basal Analysis
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
      <span>
        {(insulinStats.bolusCount ?? 0) + (insulinStats.basalCount ?? 0)} insulin events
      </span>
    </div>
    <ReliabilityBadge reliability={insulinStats?.reliability} />
  </div>

  <!-- What is this report - Educational Card -->
  <Card
    class="border-2 border-blue-200 bg-blue-50/50 dark:border-blue-800 dark:bg-blue-950/30"
  >
    <CardHeader class="pb-3">
      <CardTitle class="flex items-center gap-2 text-base">
        <HelpCircle class="h-5 w-5 text-blue-600" />
        Understanding Basal/Bolus Balance
      </CardTitle>
    </CardHeader>
    <CardContent class="space-y-2 text-sm">
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
        A typical split is around 50/50, but this can vary based on diet,
        activity, and individual needs. Some people do well with 40/60 or 60/40
        ratios.
      </p>
    </CardContent>
  </Card>

  <!-- Key Summary Stats -->
  <div class="grid grid-cols-2 gap-4 @md:grid-cols-3 @lg:grid-cols-5">
    <Card class="border @lg:col-span-1">
      <CardContent class="pt-6 text-center">
        <div class="text-3xl font-bold tabular-nums text-primary">
          {(insulinStats.tdd ?? 0).toFixed(1)}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg TDD</div>
        <div class="text-[10px] text-muted-foreground/60">units/day</div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums text-amber-600">
          {(insulinStats.basalPercent ?? 0).toFixed(0)}%
        </div>
        <div class="text-xs font-medium text-muted-foreground">Basal</div>
        <div class="text-[10px] text-muted-foreground/60">
          {(insulinStats.totalBasal ?? 0).toFixed(1)}U total
        </div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums text-blue-600">
          {(insulinStats.bolusPercent ?? 0).toFixed(0)}%
        </div>
        <div class="text-xs font-medium text-muted-foreground">Bolus</div>
        <div class="text-[10px] text-muted-foreground/60">
          {(insulinStats.totalBolus ?? 0).toFixed(1)}U total
        </div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {(insulinStats.bolusesPerDay ?? 0).toFixed(1)}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Boluses/Day</div>
        <div class="text-[10px] text-muted-foreground/60">
          avg {(insulinStats.avgBolus ?? 0).toFixed(1)}U each
        </div>
      </CardContent>
    </Card>
    <Card class="border">
      <CardContent class="pt-6 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {(insulinStats.icRatio ?? 0) > 0
            ? `1:${(insulinStats.icRatio ?? 0).toFixed(0)}`
            : "–"}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg I:C</div>
        <div class="text-[10px] text-muted-foreground/60">
          {(insulinStats.totalCarbs ?? 0).toFixed(0)}g carbs
        </div>
      </CardContent>
    </Card>
  </div>

  <!-- Ratio Banner -->
  <Card class="border border-muted">
    <CardContent class="flex items-center gap-4 py-4">
      <div class="rounded-lg bg-primary/10 p-3">
        <Target class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h3 class="font-semibold">
          Basal/Bolus Ratio: {(insulinStats.basalPercent ?? 0).toFixed(0)}% / {(insulinStats.bolusPercent ?? 0).toFixed(
            0
          )}%
        </h3>
        <p class="text-sm text-muted-foreground">
          A typical split is around 50/50; 40/60 and 60/40 are both common. Diet,
          activity and pump settings all move it.
        </p>
      </div>
    </CardContent>
  </Card>

  <!-- Daily Basal/Bolus Breakdown Chart -->
  <Card class="border">
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

  <!-- Hourly Insulin Delivery -->
  <Card class="border">
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

  <!-- Bolus Breakdown -->
  {#if (insulinStats.bolusCount ?? 0) > 0}
    <Card class="border">
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
        <div class="grid gap-4 @3xl:grid-cols-3">
          <div class="rounded-lg border bg-card p-4 text-center">
            <div class="text-3xl font-bold text-blue-600">
              {insulinStats.bolusCount ?? 0}
            </div>
            <div class="text-sm font-medium text-muted-foreground">
              Total Boluses
            </div>
            <div class="mt-1 text-xs text-muted-foreground/60">
              Over {dayCount} days
            </div>
          </div>

          <div class="rounded-lg border bg-card p-4 text-center">
            <div class="text-3xl font-bold text-green-600">
              {insulinStats.mealBoluses ?? 0}
            </div>
            <div class="text-sm font-medium text-muted-foreground">
              Meal Boluses
            </div>
            <div class="mt-1 text-xs text-muted-foreground/60">
              {(insulinStats.bolusCount ?? 0) > 0
                ? (
                    ((insulinStats.mealBoluses ?? 0) / (insulinStats.bolusCount ?? 1)) *
                    100
                  ).toFixed(0)
                : 0}% of boluses
            </div>
          </div>

          <div class="rounded-lg border bg-card p-4 text-center">
            <div class="text-3xl font-bold text-amber-600">
              {insulinStats.correctionBoluses ?? 0}
            </div>
            <div class="text-sm font-medium text-muted-foreground">
              Correction Boluses
            </div>
            <div class="mt-1 text-xs text-muted-foreground/60">
              {(insulinStats.bolusCount ?? 0) > 0
                ? (
                    ((insulinStats.correctionBoluses ?? 0) / (insulinStats.bolusCount ?? 1)) *
                    100
                  ).toFixed(0)
                : 0}% of boluses
            </div>
          </div>
        </div>

        <!-- Observations based on bolus patterns -->
        <div class="mt-4 rounded-lg border border-dashed bg-muted/30 p-4">
          <h4 class="font-medium">Bolus Pattern Observations</h4>
          <ul class="mt-2 space-y-1 text-sm text-muted-foreground">
            {#if (insulinStats.correctionBoluses ?? 0) > (insulinStats.mealBoluses ?? 0)}
              <li class="flex items-start gap-2">
                <Info class="mt-0.5 h-4 w-4 shrink-0 text-blue-500" />
                <span>
                  Correction boluses ({insulinStats.correctionBoluses ?? 0})
                  outnumber meal boluses ({insulinStats.mealBoluses ?? 0}) in this
                  period.
                </span>
              </li>
            {/if}
            {#if (insulinStats.bolusesPerDay ?? 0) < 3}
              <li class="flex items-start gap-2">
                <Info class="mt-0.5 h-4 w-4 shrink-0 text-blue-500" />
                <span>
                  Low bolus frequency — typical for low-carb diets or those with
                  significant basal coverage.
                </span>
              </li>
            {:else if (insulinStats.bolusesPerDay ?? 0) > 8}
              <li class="flex items-start gap-2">
                <Info class="mt-0.5 h-4 w-4 shrink-0 text-blue-500" />
                <span>
                  High bolus frequency — {(insulinStats.bolusesPerDay ?? 0).toFixed(
                    1
                  )} boluses per day, which may include many small corrections.
                </span>
              </li>
            {/if}
            {#if (insulinStats.avgBolus ?? 0) > 0}
              <li class="flex items-start gap-2">
                <TrendingUp class="mt-0.5 h-4 w-4 shrink-0 text-green-500" />
                <span>
                  Average bolus size of {(insulinStats.avgBolus ?? 0).toFixed(1)}U —
                  {#if (insulinStats.avgBolus ?? 0) < 2}
                    smaller boluses may indicate frequent snacking or active
                    lifestyle.
                  {:else if (insulinStats.avgBolus ?? 0) > 8}
                    larger boluses typical for higher carb meals.
                  {:else}
                    moderate bolus sizes.
                  {/if}
                </span>
              </li>
            {/if}
          </ul>
        </div>
      </CardContent>
    </Card>
  {/if}

  <!-- Clinical Notes -->
  <Card class="border bg-muted/30">
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <Layers class="h-5 w-5 text-muted-foreground" />
        Clinical Reference
      </CardTitle>
    </CardHeader>
    <CardContent class="space-y-3 text-sm text-muted-foreground">
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

  <!-- Navigation -->
  <Separator class="print:hidden" />
  <div class="flex flex-wrap items-center justify-center gap-2 print:hidden">
    <Button href="/reports" variant="outline" size="sm">← All Reports</Button>
    <Button href="/reports/basal-analysis" size="sm" class="gap-2">
      Basal Rate Analysis
      <ArrowRight class="h-4 w-4" />
    </Button>
    <Button href="/reports/treatments" variant="outline" size="sm">
      Treatment Log
    </Button>
  </div>

  <!-- Footer -->
  <div class="space-y-1 text-center text-xs text-muted-foreground">
    <p>
      Report generated from {(insulinStats.bolusCount ?? 0).toLocaleString()} boluses between
      {startDate.toLocaleDateString()} and {endDate.toLocaleDateString()}
    </p>
    <p class="text-muted-foreground/60">
      This report is for informational purposes only. Always consult your
      healthcare provider for medical advice.
    </p>
  </div>
</div>
{/if}
