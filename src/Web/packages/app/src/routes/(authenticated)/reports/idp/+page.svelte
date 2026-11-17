<script lang="ts">
  import { EmptyState } from "#lib/components/shared/index.js";
  import {
    a1cLabel,
    a1cUnitLabel,
    formatA1cValue,
  } from "#lib/utils/a1c-formatting.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Calendar from "@lucide/svelte/icons/calendar";
  import Target from "@lucide/svelte/icons/target";
  import Activity from "@lucide/svelte/icons/activity";
  import Droplets from "@lucide/svelte/icons/droplets";
  import { AmbulatoryGlucoseProfile } from "#lib/components/ambulatory-glucose-profile/index.js";
  import TIRStackedChart from "#lib/components/reports/TIRStackedChart.svelte";
  import ReliabilityBadge from "#lib/components/reports/ReliabilityBadge.svelte";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import FigureStrip from "#lib/components/reports/FigureStrip.svelte";
  import GlycemicRiskIndexChart from "#lib/components/reports/GlycemicRiskIndexChart.svelte";
  import ScheduledBasalRateChart from "#lib/components/reports/ScheduledBasalRateChart.svelte";
  import HourlyBolusChart from "#lib/components/reports/HourlyBolusChart.svelte";
  import ScheduleFooter from "#lib/components/reports/ScheduleFooter.svelte";
  import ActiveProfileHeading from "#lib/components/reports/ActiveProfileHeading.svelte";
  import AidSystemUseCard from "#lib/components/reports/AidSystemUseCard.svelte";
  import TextureSwatch from "#lib/components/charts/print/TextureSwatch.svelte";
  import { bgPatternClass } from "#lib/components/charts/print/chart-print-patterns.js";
  import { getIdpData } from "#lib/api/idp.remote.js";
  import { bg, bgLabel, formatMediumDateTime, formatNumber, formatNumericDate } from "#lib/utils/formatting.js";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is the standard IDP report period
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration; `date` carries the
  // selected range, so the page has one day count rather than two.
  const reportsResource = contextResource(
    () => getIdpData(reportsParams.dateRangeInput),
    { errorTitle: "Error loading IDP report", dateParams: reportsParams }
  );

  const data = $derived({
    readingCount: reportsResource.current?.readingCount ?? 0,
    hourlyBolusCounts: reportsResource.current?.hourlyBolusCounts ?? [],
    hourlyTimeZoneUnavailableReason: reportsResource.current?.hourlyTimeZoneUnavailableReason,
    insulinDeliveryStats: reportsResource.current?.insulinDeliveryStats,
    activeSchedules: reportsResource.current?.activeSchedules ?? [],
    analysis: reportsResource.current?.analysis,
    averagedStats: reportsResource.current?.averagedStats,
    aidSystemMetrics: reportsResource.current?.aidSystemMetrics,
    timeZoneUnavailableReason: reportsResource.current?.timeZoneUnavailableReason,
  });

  // Derived values from data
  const readingCount = $derived(data.readingCount);
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

{#if reportsResource.current}
<div class="@container space-y-8">
  <div class="space-y-4">
    <div class="print:hidden">
      <p class="text-muted-foreground">
        Comprehensive insulin delivery analysis with glucose context
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
      <Calendar class="w-4 h-4 print:hidden" />
      <span class="print:hidden">
        {formatNumericDate(startDate)} – {formatNumericDate(endDate)}
      </span>
      <span class="text-muted-foreground/50 print:hidden">•</span>
      <span class="print:hidden">{dayCount} days</span>
      <span class="text-muted-foreground/50 print:hidden">•</span>
      <span>{formatNumber(readingCount)} readings</span>
    </div>
  </div>

  <FigureStrip
    figures={[
      { label: "Avg total daily dose", value: insulinStats?.tdd?.toFixed(1) ?? "--", unit: "U/day" },
      { label: "Average", value: stats.mean ? String(bg(stats.mean)) : "--", unit: bgLabel() },
      { label: a1cLabel(true), value: formatA1cValue(variability.estimatedA1cDisplay), unit: a1cUnitLabel(), note: "From mean glucose", },
      { label: "CV", value: variability.coefficientOfVariation?.toFixed(0) ?? "--", unit: "%", note: variability.coefficientOfVariationTarget != null ? `Target: ≤${variability.coefficientOfVariationTarget}%` : undefined },
    ]}
  />

  {#if analysis?.reliability}
    <ReliabilityBadge reliability={analysis.reliability} />
  {/if}

  {#if data.timeZoneUnavailableReason}
    <p class="text-sm text-muted-foreground">
      <TimeZoneNote reason={data.timeZoneUnavailableReason}>
        The hourly glucose figures place each reading at the local time its device reported. Days of data
        are counted on UTC days.
      </TimeZoneNote>
    </p>
  {/if}

  <div class="grid grid-cols-1 @3xl:grid-cols-2 print:grid-cols-2 gap-6">
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Syringe class="w-5 h-5 text-insulin" />
          Insulin summary
        </CardTitle>
        <CardDescription>
          Daily insulin delivery breakdown
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {@const basalPct = insulinStats?.basalPercent ?? 0}
        {@const bolusPct = insulinStats?.bolusPercent ?? 0}
        <div class="space-y-1">
          <div class="flex justify-between text-xs text-muted-foreground">
            <span class="flex items-center gap-1">
              <TextureSwatch texture="insulin-scheduled-basal" color="var(--basal)" />
              Basal: {insulinStats?.totalBasalPerDay?.toFixed(1) ?? "--"} U/day ({basalPct.toFixed(0)}%)
            </span>
            <span class="flex items-center gap-1">
              <TextureSwatch texture="insulin-bolus" />
              Bolus: {insulinStats?.totalBolusPerDay?.toFixed(1) ?? "--"} U/day ({bolusPct.toFixed(0)}%)
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
          <p class="text-xs text-muted-foreground tabular-nums">
            Bolus total: manual {(insulinStats?.manualBolus ?? 0).toFixed(1)} U / automatic {(insulinStats?.automaticBolus ?? 0).toFixed(1)} U
          </p>
        </div>

        <Separator />

        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <div class="text-muted-foreground">Avg delivered basal</div>
            <div class="font-semibold">{insulinStats?.totalBasalPerDay?.toFixed(1) ?? "--"} U/day</div>
          </div>
          <div>
            <div class="text-muted-foreground">Avg scheduled basal</div>
            <div class="font-semibold">{insulinStats?.scheduledBasalPerDay?.toFixed(1) ?? "--"} U/day</div>
          </div>
        </div>

        <Separator />

        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <div class="text-muted-foreground">Boluses/Day</div>
            <div class="font-semibold">{insulinStats?.bolusesPerDay?.toFixed(1) ?? "--"}</div>
          </div>
          <div>
            <div class="text-muted-foreground">Avg bolus size</div>
            <div class="font-semibold">{insulinStats?.avgBolus?.toFixed(1) ?? "--"} U</div>
          </div>
          <div>
            <div class="text-muted-foreground">Meal boluses/day</div>
            <div class="font-semibold">
              {insulinStats?.mealBolusesPerDay?.toFixed(1) ?? "--"}
            </div>
          </div>
          <div>
            <div class="text-muted-foreground">Correction boluses/day</div>
            <div class="font-semibold">
              {insulinStats?.correctionBolusesPerDay?.toFixed(1) ?? "--"}
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Droplets class="w-5 h-5 text-glucose-in-range" />
          Glucose metrics
        </CardTitle>
        <CardDescription>
          Key glucose indicators for this period
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {#if analysis}
          {@const tir = analysis.timeInRange?.percentages ?? {}}
          <div class="space-y-2">
            <div class="text-sm font-medium">Time in range</div>
            <div class="h-56 w-full">
              <TIRStackedChart percentages={tir} />
            </div>
          </div>
        {:else}
          <EmptyState art="report-pages" size="compact" title="No glucose analysis available" />
        {/if}
      </CardContent>
    </Card>
  </div>

  <div class="grid grid-cols-1 @3xl:grid-cols-2 gap-6">
    <AidSystemUseCard metrics={aidMetrics} />

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Target class="w-5 h-5" />
          Glycemic risk index
        </CardTitle>
        <CardDescription>
          Composite metric of hypo and hyperglycemia risk
        </CardDescription>
      </CardHeader>
      <CardContent>
        {#if analysis?.gri}
          <GlycemicRiskIndexChart gri={analysis.gri} />
        {:else}
          <EmptyState art="report-pages" size="compact" title="No GRI data available" class="h-full" />
        {/if}
      </CardContent>
    </Card>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Activity class="w-5 h-5" />
        Glucose pattern
      </CardTitle>
      <CardDescription>
        Daily glucose overlay with basal rate, bolus distribution, and dosing profile
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-6">
      <div class="h-80 w-full @2xl:h-96">
        <AmbulatoryGlucoseProfile averagedStats={data.averagedStats} thresholds={analysis?.timeInRange?.thresholds} />
      </div>

      <div>
        <h4 class="text-sm font-semibold text-muted-foreground mb-1">Scheduled basal rate</h4>
        {#each data.activeSchedules as profile (profile.profileName)}
          <ActiveProfileHeading {profile} multiple={data.activeSchedules.length > 1} />
          <div class="h-24 w-full">
            <ScheduledBasalRateChart entries={profile.basal ?? []} />
          </div>
        {:else}
          <div class="h-24 w-full"></div>
        {/each}
      </div>

      <div class="w-full">
        <h4 class="text-sm font-semibold text-muted-foreground mb-1">User-initiated boluses per day</h4>
        {#if data.hourlyTimeZoneUnavailableReason}
          <p class="text-xs text-muted-foreground">This chart cannot show boluses by hour of day.</p>
        {:else}
          <HourlyBolusChart hours={data.hourlyBolusCounts} />
        {/if}
      </div>

      <ScheduleFooter profiles={data.activeSchedules} />
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
