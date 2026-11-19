<script lang="ts">
  import {
    formatInsulinToCarbRatio,
    formatNumber,
    formatNumericDate,
    unitsPerTenGrams,
    writesUnitsPerTenGrams,
  } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import PieChart from "@lucide/svelte/icons/chart-pie";
  import Calendar from "@lucide/svelte/icons/calendar";
  import Info from "@lucide/svelte/icons/info";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import HelpCircle from "@lucide/svelte/icons/circle-question-mark";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Layers from "@lucide/svelte/icons/layers";
  import BasalBolusRatioChart from "#lib/components/reports/BasalBolusRatioChart.svelte";
  import InsulinDeliveryChart from "#lib/components/reports/InsulinDeliveryChart.svelte";
  import ReliabilityBadge from "#lib/components/reports/ReliabilityBadge.svelte";
  import FigureStrip from "#lib/components/reports/FigureStrip.svelte";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import type { InsulinDeliveryStatistics } from "#lib/api/index.js";
  import {
    getDailyBasalBolusReport,
    getHourlyDeliveryReport,
    getInsulinDeliveryReport,
  } from "#lib/api/reports.remote.js";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";

  // Get shared date params from context (set by reports layout)
  // Default: 30 days for insulin delivery analysis (TDD and ratios benefit from more data)
  const reportsParams = requireDateParamsContext(30);

  // The picked days, read in the patient's zone by the server.
  const range = $derived({ from: reportsParams.fromDay, to: reportsParams.toDay });

  // contextResource keeps a resolved response across superseded query instances; a bare
  // $derived query strands it on a superseded one (sveltejs/kit#14915).
  const dailyRatiosResource = contextResource(
    () => getDailyBasalBolusReport(range),
    { errorTitle: "Error loading insulin delivery data" }
  );

  // Headline insulin figures for the selected range.
  const insulinResource = contextResource(
    () => getInsulinDeliveryReport(range),
    {
      errorTitle: "Error loading insulin delivery data",
      dateParams: reportsParams,
    }
  );

  // Hourly delivery pattern with automatic layout registration. Computed
  // backend-side from pump-confirmed records, bucketed by the user's timezone.
  const hourlyDeliveryResource = contextResource(
    () => getHourlyDeliveryReport(range),
    { errorTitle: "Error loading insulin delivery data" }
  );
  const hourlyDelivery = $derived(hourlyDeliveryResource.current);
  const timeZoneReason = $derived(
    dailyRatiosResource.current?.timeZoneUnavailableReason ??
      hourlyDelivery?.timeZoneUnavailableReason
  );

  const emptyStats: InsulinDeliveryStatistics = {
    totalBolus: 0,
    totalBasal: 0,
    totalInsulin: 0,
    totalCarbs: 0,
    manualBolus: 0,
    automaticBolus: 0,
    manualBolusCount: 0,
    automaticBolusCount: 0,
    basalCount: 0,
    basalPercent: 0,
    bolusPercent: 0,
    tdd: 0,
    avgBolus: 0,
    mealBoluses: 0,
    correctionBoluses: 0,
    unpairedCarbs: 0,
    bolusesPerDay: 0,
    carbCount: 0,
    carbBolusCount: 0,
  };

  const insulinStats = $derived(insulinResource.current ?? emptyStats);

  const startDate = $derived(insulinResource.date.from);
  const endDate = $derived(insulinResource.date.to);
  const dayCount = $derived(insulinResource.date.dayCount);
</script>

{#if insulinResource.current}
<div class="@container space-y-8">
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4 print:hidden">
      <div>
        <p class="text-muted-foreground">
          Comprehensive analysis of your basal and bolus insulin patterns
        </p>
      </div>
      <div class="flex items-center gap-2">
        <Button
          href="/reports/basal-analysis"
          variant="outline"
          size="sm"
        >
          Basal analysis
          <ArrowRight class="h-4 w-4" />
        </Button>
      </div>
    </div>

    <div class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground print:hidden">
      <Calendar class="h-4 w-4" />
      <span>
        {formatNumericDate(startDate)} – {formatNumericDate(endDate)}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{dayCount} days</span>
      <span class="text-muted-foreground/50">•</span>
      <span>
        {insulinStats.insulinEventCount ?? 0} insulin events
      </span>
    </div>
    <ReliabilityBadge reliability={insulinStats?.reliability} />
  </div>

  <Card variant="info">
    <CardHeader class="pb-3">
      <CardTitle class="flex items-center gap-2 text-base">
        <HelpCircle class="h-5 w-5 text-info" />
        Understanding basal/bolus balance
      </CardTitle>
    </CardHeader>
    <CardContent size="sm" class="space-y-2">
      <p>
        Your <strong>total daily dose (TDD)</strong>
        is split between two types of insulin:
      </p>
      <ul class="list-inside list-disc space-y-1 pl-2 text-muted-foreground">
        <li>
          <strong>Basal insulin:</strong>
          Continuous background insulin that covers your body's baseline needs
        </li>
        <li>
          <strong>Bolus insulin:</strong>
          Insulin taken for meals and to correct high glucose, plus automatic
          boluses delivered by a loop
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
      { label: "Bolus", value: (insulinStats.bolusPercent ?? 0).toFixed(0), unit: "%", note: `${(insulinStats.totalBolus ?? 0).toFixed(1)}U total`, detail: `manual ${(insulinStats.manualBolus ?? 0).toFixed(1)} U / automatic ${(insulinStats.automaticBolus ?? 0).toFixed(1)} U` },
      { label: "Boluses/Day", value: (insulinStats.bolusesPerDay ?? 0).toFixed(1), note: `avg ${(insulinStats.avgBolus ?? 0).toFixed(1)}U each` },
      { label: "ICR", value: formatInsulinToCarbRatio(insulinStats.icRatio), note: (insulinStats.unpairedCarbs ?? 0) > 0 ? `${(insulinStats.unpairedCarbs ?? 0).toFixed(0)} g not paired with a bolus` : undefined },
    ]}
  />

  {#if timeZoneReason}
    <p class="text-sm text-muted-foreground">
      <TimeZoneNote reason={timeZoneReason}>
        This report cannot show insulin by local day or hour of day.
      </TimeZoneNote>
    </p>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <PieChart class="h-5 w-5 text-muted-foreground" />
        Daily basal/bolus breakdown
      </CardTitle>
      <CardDescription>
        See how your insulin was split each day, against the carbs you logged
      </CardDescription>
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
        Hourly insulin delivery
      </CardTitle>
      <CardDescription>
        Average insulin delivered by hour of day, split by basal, manual bolus and automatic bolus
      </CardDescription>
    </CardHeader>
    <CardContent>
      <InsulinDeliveryChart delivery={hourlyDelivery} showStacked={true} />
    </CardContent>
  </Card>

  {#if (insulinStats.manualBolusCount ?? 0) > 0}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Info class="h-5 w-5 text-muted-foreground" />
          Bolus breakdown
        </CardTitle>
        <CardDescription>
          Understanding your bolus insulin usage
        </CardDescription>
      </CardHeader>
      <CardContent>
        <dl class="m-0 divide-y divide-border">
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 py-2 first:pt-0">
            <dt class="text-sm font-medium text-muted-foreground">Total boluses</dt>
            <dd class="m-0 flex items-baseline gap-2">
              <span class="text-lg font-semibold tabular-nums">{insulinStats.manualBolusCount ?? 0}</span>
              <span class="text-xs text-muted-foreground">Over {dayCount} days</span>
            </dd>
          </div>
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 py-2">
            <dt class="text-sm font-medium text-muted-foreground">Meal boluses</dt>
            <dd class="m-0 flex items-baseline gap-2">
              <span class="text-lg font-semibold tabular-nums">{insulinStats.mealBoluses ?? 0}</span>
              <span class="text-xs text-muted-foreground">
                {(insulinStats.mealBolusPercent ?? 0).toFixed(0)}% of boluses
              </span>
            </dd>
          </div>
          <div class="flex flex-wrap items-baseline justify-between gap-x-4 py-2">
            <dt class="text-sm font-medium text-muted-foreground">Correction boluses</dt>
            <dd class="m-0 flex items-baseline gap-2">
              <span class="text-lg font-semibold tabular-nums">{insulinStats.correctionBoluses ?? 0}</span>
              <span class="text-xs text-muted-foreground">
                {(insulinStats.correctionBolusPercent ?? 0).toFixed(0)}% of boluses
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
        Clinical reference
      </CardTitle>
    </CardHeader>
    <CardContent variant="muted" class="space-y-3">
      <p>
        <strong>Total daily dose (TDD):</strong>
        Typically ranges from 0.4-1.0 units/kg body weight for Type 1 diabetes. Your
        TDD of
        <strong>{(insulinStats.tdd ?? 0).toFixed(1)}U/day</strong>
        can be compared to this reference.
      </p>
      {#if insulinStats.icRatio != null && writesUnitsPerTenGrams()}
        <p>
          <strong>ICR check:</strong>
          Your insulin-to-carb ratio of {formatInsulinToCarbRatio(insulinStats.icRatio)}
          means you use about {unitsPerTenGrams(insulinStats.icRatio).toFixed(2)} units of insulin
          for every 10 grams of carbs.
        </p>
      {:else if insulinStats.icRatio != null}
        <p>
          <strong>ICR check:</strong>
          Your insulin-to-carb ratio of {formatInsulinToCarbRatio(insulinStats.icRatio)}
          means you use about 1 unit of insulin for every {insulinStats.icRatio.toFixed(1)}
          grams of carbs.
        </p>
      {/if}
    </CardContent>
  </Card>

  <Separator class="print:hidden" />
  <div class="flex flex-wrap items-center justify-center gap-2 print:hidden">
    <Button href="/reports" variant="outline" size="sm">
      <ArrowLeft class="h-4 w-4" />
      All reports
    </Button>
    <Button href="/reports/basal-analysis" size="sm">
      Basal rate analysis
      <ArrowRight class="h-4 w-4" />
    </Button>
    <Button href="/reports/treatments" variant="outline" size="sm">
      Treatment log
    </Button>
  </div>

  <div class="space-y-1 text-center text-xs text-muted-foreground">
    <p class="print:hidden">
      Report generated from {formatNumber(insulinStats.manualBolusCount)} manual boluses between
      {formatNumericDate(startDate)} and {formatNumericDate(endDate)}
    </p>
    <p class="text-muted-foreground/60">
      This report is for informational purposes only. Always consult your
      healthcare provider for medical advice.
    </p>
  </div>
</div>
{/if}
