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
    Layers,
    Calendar,
    Info,
    ArrowRight,
    ArrowLeft,
    HelpCircle,
    Clock,
    Gauge,
  } from "lucide-svelte";
  import BasalRatePercentileChart from "$lib/components/reports/BasalRatePercentileChart.svelte";
  import InsulinDeliveryChart from "$lib/components/reports/InsulinDeliveryChart.svelte";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import {
    getBasalAnalysis,
    getHourlyInsulinDelivery,
  } from "$api/generated/statistics.generated.remote";

  interface Props {
    analysisDates: { startDate: string; endDate: string };
    dateInfo: { from: Date; to: Date; dayCount: number };
  }
  let { analysisDates, dateInfo }: Props = $props();

  // Query instances are created once per component instance (the parent remounts
  // this component via {#key} when the date range changes). Creating a query
  // inside $derived and polling .loading/.current can strand the resolved
  // response on a superseded instance (sveltejs/kit#14915), leaving .loading
  // stuck at true and the page blank. Component-level instances avoid the race.
  const hourlyDeliveryQuery = getHourlyInsulinDelivery(analysisDates);
  const analysisQuery = getBasalAnalysis(analysisDates);

  const hourlyDelivery = $derived(hourlyDeliveryQuery.current?.hours ?? []);

  const basalStats = $derived.by(() => {
    const s = analysisQuery.current?.stats;
    return {
      count: s?.count ?? 0,
      avgRate: s?.avgRate ?? 0,
      minRate: s?.minRate ?? 0,
      maxRate: s?.maxRate ?? 0,
      totalDelivered: s?.totalDelivered ?? 0,
    };
  });

  const tempBasalInfo = $derived.by(() => {
    const t = analysisQuery.current?.tempBasalInfo;
    return {
      total: t?.total ?? 0,
      perDay: t?.perDay ?? 0,
      highTemps: t?.highTemps ?? 0,
      lowTemps: t?.lowTemps ?? 0,
      zeroTemps: t?.zeroTemps ?? 0,
    };
  });

  const hourlyPercentiles = $derived(analysisQuery.current?.hourlyPercentiles ?? []);
</script>

{#if hourlyDeliveryQuery.error || analysisQuery.error}
  <div class="@container container mx-auto max-w-7xl p-3 @md:p-6">
    <Card variant="destructive">
      <CardHeader>
        <CardTitle variant="destructive" class="flex items-center gap-2 text-base">
          <Info class="h-5 w-5" />
          Error Loading Basal Analysis
        </CardTitle>
      </CardHeader>
      <CardContent variant="muted">
        {String(hourlyDeliveryQuery.error ?? analysisQuery.error)}
      </CardContent>
    </Card>
  </div>
{:else if !hourlyDeliveryQuery.current}
  <ReportsSkeleton />
{:else}
  <div class="@container container mx-auto max-w-7xl space-y-8 p-3 @md:p-6">
    <div class="space-y-4">
      <div class="flex flex-wrap items-center justify-between gap-4 print:hidden">
        <div>
          <h1 class="flex items-center gap-3 text-2xl font-bold @md:text-3xl">
            <Layers class="h-6 w-6 text-report-treatment @md:h-8 @md:w-8" />
            Basal Rate Analysis
          </h1>
          <p class="mt-1 text-muted-foreground">
            Understand your background insulin delivery patterns over time
          </p>
        </div>
        <div class="flex items-center gap-2">
          <Button
            href="/reports/insulin-delivery"
            variant="outline"
            size="sm"
          >
            Insulin Delivery
            <ArrowRight class="h-4 w-4" />
          </Button>
        </div>
      </div>

      <div
        class="flex flex-wrap items-center gap-2 text-sm text-muted-foreground print:hidden"
      >
        <Calendar class="h-4 w-4" />
        <span>
          {formatNumericDate(dateInfo.from)} – {formatNumericDate(dateInfo.to)}
        </span>
        <span class="text-muted-foreground/50">•</span>
        <span>{dateInfo.dayCount} days</span>
        <span class="text-muted-foreground/50">•</span>
        <span>{basalStats.count} basal events</span>
      </div>
    </div>

    <Card variant="info">
      <CardHeader class="pb-3">
        <CardTitle class="flex items-center gap-2 text-base">
          <HelpCircle class="h-5 w-5 text-info" />
          Understanding This Report
        </CardTitle>
      </CardHeader>
      <CardContent size="sm" class="space-y-2">
        <p>
          This report shows how your <strong>
            basal (background) insulin delivery
          </strong>
          varies throughout the day. The percentile chart shows your typical patterns:
        </p>
        <ul class="list-inside list-disc space-y-1 pl-2 text-muted-foreground">
          <li>
            The <strong>median line</strong>
            shows your most common basal rate at each hour
          </li>
          <li>
            The <strong>shaded bands</strong>
            show the range of variation (10th-90th percentile)
          </li>
          <li>Wider bands mean the delivered rate varied more at that hour</li>
        </ul>
      </CardContent>
    </Card>

    <FigureStrip
      figures={[
        { label: "Avg Rate", value: basalStats.avgRate.toFixed(2), unit: "U/hr" },
        { label: "Total Basal", value: basalStats.totalDelivered.toFixed(1), unit: "units delivered" },
        { label: "Temp Basals", value: tempBasalInfo.perDay.toFixed(1), unit: "per day avg" },
        { label: "High / Low", value: `${tempBasalInfo.highTemps} / ${tempBasalInfo.lowTemps}`, unit: "temp basals" },
      ]}
    />

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Gauge class="h-5 w-5 text-muted-foreground" />
          Basal Rate Percentile Chart
        </CardTitle>
        <CardDescription>
          Your typical basal delivery pattern across 24 hours (like an AGP for
          basal rates)
        </CardDescription>
      </CardHeader>
      <CardContent>
        <BasalRatePercentileChart
          data={hourlyPercentiles}
          loading={analysisQuery.loading}
        />
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Clock class="h-5 w-5 text-muted-foreground" />
          Average Hourly Basal Delivery
        </CardTitle>
        <CardDescription>
          Average basal insulin delivered per hour of the day
        </CardDescription>
      </CardHeader>
      <CardContent>
        <InsulinDeliveryChart data={hourlyDelivery} showStacked={false} />
      </CardContent>
    </Card>

    {#if basalStats.count > 0}
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Info class="h-5 w-5 text-muted-foreground" />
            Basal Insights
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div class="divide-y divide-border">
            <div class="py-3 first:pt-0">
              <h4 class="font-medium">Basal Rate Range</h4>
              <p class="text-sm text-muted-foreground">
                Your basal rates ranged from
                <strong>{basalStats.minRate.toFixed(2)} U/hr</strong>
                to <strong>{basalStats.maxRate.toFixed(2)} U/hr</strong>.
              </p>
            </div>

            <div class="py-3">
              <h4 class="font-medium">Temp Basal Activity</h4>
              <p class="text-sm text-muted-foreground">
                {#if tempBasalInfo.perDay > 0}
                  <strong class="tabular-nums">{tempBasalInfo.perDay.toFixed(1)}</strong>
                  temp basals per day on average.
                {:else}
                  No temp basal activity recorded in this period.
                {/if}
              </p>
            </div>

            {#if tempBasalInfo.zeroTemps > 0}
              <div class="py-3">
                <h4 class="font-medium">Suspend/Zero Temp Basals</h4>
                <p class="text-sm text-muted-foreground">
                  <strong>{tempBasalInfo.zeroTemps}</strong>
                  zero or suspend temp basals were recorded.
                </p>
              </div>
            {/if}

            <div class="py-3 last:pb-0">
              <h4 class="font-medium">Daily Basal Insulin</h4>
              <p class="text-sm text-muted-foreground">
                Average of <strong>
                  {(basalStats.totalDelivered / dateInfo.dayCount).toFixed(1)} units
                </strong>
                of basal insulin delivered per day over this {dateInfo.dayCount}-day
                period.
              </p>
            </div>
          </div>
        </CardContent>
      </Card>
    {/if}

    <Separator class="print:hidden" />
    <div class="flex flex-wrap items-center justify-center gap-2 print:hidden">
      <Button href="/reports" variant="outline" size="sm">
        <ArrowLeft class="h-4 w-4" />
        All Reports
      </Button>
      <Button href="/reports/insulin-delivery" size="sm">
        Insulin Delivery Report
        <ArrowRight class="h-4 w-4" />
      </Button>
      <Button href="/reports/treatments" variant="outline" size="sm">
        Treatment Log
      </Button>
    </div>

    <div class="space-y-1 text-center text-xs text-muted-foreground">
      <p class="print:hidden">
        Report generated from {formatNumber(basalStats.count)} basal events between
        {formatNumericDate(dateInfo.from)} and {formatNumericDate(dateInfo.to)}
      </p>
      <p class="text-muted-foreground/60">
        This report is for informational purposes only. Always consult your
        healthcare provider for medical advice.
      </p>
    </div>
  </div>
{/if}
