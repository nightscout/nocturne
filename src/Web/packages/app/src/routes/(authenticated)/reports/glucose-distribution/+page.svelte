<script lang="ts">
  import { PieChart, Text } from "layerchart";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { Button } from "$lib/components/ui/button";
  import { getReportsAnalysis } from "$api/reports.remote";
  import HourlyGlucoseDistributionChart from "$lib/components/reports/HourlyGlucoseDistributionChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import FigureStrip from "$lib/components/reports/FigureStrip.svelte";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import TextureSwatch from "$lib/components/charts/print/TextureSwatch.svelte";
  import {
    CHART_TEXTURES,
    patternClass,
    type GlucoseRange,
  } from "$lib/components/charts/print/chart-print-patterns";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg, bgLabel, formatShortDate } from "$lib/utils/formatting";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";

  const reportsParams = requireDateParamsContext(14);

  const reportsResource = contextResource(
    () => getReportsAnalysis(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Glucose Distribution" }
  );

  // In the URL so the chart the user is looking at can be refreshed and shared.
  const viewParams = useSearchParams(
    z.object({ tightRange: z.enum(["show", "hide"]).nullable().default(null) }),
    { showDefaults: false, noScroll: true }
  );
  const showTightRange = $derived(viewParams.tightRange !== "hide");

  const rangeStats = $derived.by(() => {
    const tir =
      reportsResource.current?.analysis?.timeInRange?.percentages;

    const range = (key: string, texture: GlucoseRange, value: number) => ({
      key,
      texture,
      color: CHART_TEXTURES[texture].color,
      value,
      props: { class: patternClass(texture) },
    });

    return [
      range("Very Low", "very-low", tir?.veryLow ?? 0),
      range("Low", "low", tir?.low ?? 0),
      ...(showTightRange
        ? [
            range("Tight Range", "tight-range", tir?.tightTarget ?? 0),
            range("In Range", "in-range", (tir?.target ?? 0) - (tir?.tightTarget ?? 0)),
          ]
        : [range("In Range", "in-range", tir?.target ?? 0)]),
      range("High", "high", tir?.high ?? 0),
      range("Very High", "very-high", tir?.veryHigh ?? 0),
    ];
  });

  const tirPercentage = $derived(
    reportsResource.current?.analysis?.timeInRange?.percentages?.target ?? 0
  );

  /** No readings in the window means nothing on this page has been measured. */
  const hasReadings = $derived(
    (reportsResource.current?.analysis?.basicStats?.count ?? 0) > 0
  );

  const overallStats = $derived.by(() => {
    const analysis = reportsResource.current?.analysis;
    const basicStats = analysis?.basicStats;
    const glycemicVariability = analysis?.glycemicVariability;

    return {
      totalReadings: basicStats?.count ?? 0,
      mean: basicStats?.mean ?? 0,
      median: basicStats?.median ?? 0,
      stdDev: basicStats?.standardDeviation ?? 0,
      // The A1c estimate is computed by the backend; there is no frontend fallback.
      a1cDCCT: analysis?.gmi?.value ?? glycemicVariability?.estimatedA1c ?? null,
      gvi: glycemicVariability?.glycemicVariabilityIndex ?? null,
      pgs: glycemicVariability?.patientGlycemicStatus ?? null,
      meanTotalDailyChange: glycemicVariability?.meanTotalDailyChange ?? null,
      timeInFluctuation: glycemicVariability?.timeInFluctuation ?? null,
    };
  });

  const dateRangeDisplay = $derived.by(() => {
    const dateRange = reportsResource.current?.dateRange;
    if (!dateRange) return "";
    return `${formatShortDate(dateRange.from, true)} – ${formatShortDate(dateRange.to, true)}`;
  });
</script>

{#if reportsResource.current}
  {@const report = reportsResource.current}
  <div class="@container space-y-6 p-3 @md:p-6">
    <header class="print:hidden">
      <h1 class="text-2xl font-bold">Glucose Distribution</h1>
      <p class="mt-1 text-sm text-muted-foreground">
        {dateRangeDisplay} • {overallStats.totalReadings} readings
      </p>
    </header>

    {#if !hasReadings}
      <Card.Root>
        <Card.Content class="py-12 text-center">
          <p class="font-medium">No readings in this date range</p>
          <p class="mt-1 text-sm text-muted-foreground">
            Distribution, A1c estimation and variability statistics need glucose
            readings to be calculated. Try a wider date range.
          </p>
        </Card.Content>
      </Card.Root>
    {:else}
      <FigureStrip
        figures={[
          { label: "Mean", value: String(bg(overallStats.mean)), unit: bgLabel() },
          { label: "Median", value: String(bg(overallStats.median)), unit: bgLabel() },
          { label: "Std Dev", value: String(bg(overallStats.stdDev)), unit: bgLabel() },
          { label: "Readings", value: String(overallStats.totalReadings) },
        ]}
      />

      <div class="grid gap-6 @3xl:grid-cols-2 print:grid-cols-2">
        <Card.Root>
          <Card.Header>
            <div class="flex items-center justify-between">
              <Card.Title class="text-lg">Distribution Chart</Card.Title>
              <Button
                variant="ghost"
                size="sm"
                class="print:hidden"
                onclick={() =>
                  (viewParams.tightRange = showTightRange ? "hide" : "show")}
              >
                {showTightRange ? "Hide" : "Show"} Tight Range
              </Button>
            </div>
          </Card.Header>
          <Card.Content>
            <div class="flex flex-col items-center">
              {#if rangeStats.some((d) => d.value > 0)}
                <div class="h-[300px] w-full">
                  <PieChart
                    data={rangeStats}
                    value="value"
                    cRange={rangeStats.map((s) => s.color)}
                    innerRadius={-60}
                    cornerRadius={3}
                    padAngle={0.02}
                  >
                    {#snippet aboveMarks()}
                      <Text
                        value={`${tirPercentage.toFixed(0)}%`}
                        textAnchor="middle"
                        verticalAnchor="middle"
                        dy={-8}
                        class="fill-foreground text-2xl font-bold"
                      />
                      <Text
                        value="In Range"
                        textAnchor="middle"
                        verticalAnchor="middle"
                        dy={16}
                        class="fill-muted-foreground text-xs"
                      />
                    {/snippet}
                  </PieChart>
                </div>
                <ChartKey
                  class="pt-2"
                  items={rangeStats.map((stat) => ({ texture: stat.texture, label: stat.key }))}
                />
              {:else}
                <div
                  class="flex h-[300px] items-center justify-center text-muted-foreground"
                >
                  No data available
                </div>
              {/if}
            </div>
          </Card.Content>
        </Card.Root>

        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">Distribution Statistics</Card.Title>
          </Card.Header>
          <Card.Content>
            <div class="overflow-x-auto print:overflow-visible">
              <Table.Root>
                <Table.Header>
                  <Table.Row>
                    <Table.Head>Range</Table.Head>
                    <Table.Head class="text-right">Time (%)</Table.Head>
                  </Table.Row>
                </Table.Header>
                <Table.Body>
                  {#each rangeStats as stat (stat.key)}
                    <Table.Row>
                      <Table.Cell>
                        <div class="flex items-center gap-2">
                          <TextureSwatch texture={stat.texture} />
                          {stat.key}
                        </div>
                      </Table.Cell>
                      <Table.Cell class="text-right font-medium">
                        {stat.value.toFixed(1)}%
                      </Table.Cell>
                    </Table.Row>
                  {/each}
                </Table.Body>
              </Table.Root>
            </div>
          </Card.Content>
        </Card.Root>
      </div>

      <Card.Root>
        <Card.Header>
          <Card.Title class="text-lg">Hourly Distribution</Card.Title>
          <Card.Description>
            Percentage of time in each glucose range by hour of day
          </Card.Description>
        </Card.Header>
        <Card.Content>
          <HourlyGlucoseDistributionChart averagedStats={report.averagedStats} />
        </Card.Content>
      </Card.Root>

      <div class="grid gap-6 @2xl:grid-cols-2 @4xl:grid-cols-3 print:grid-cols-3 print:gap-3">
        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">A1c Estimation</Card.Title>
            <Card.Description>Based on average glucose</Card.Description>
          </Card.Header>
          <Card.Content>
            <div class="mb-3 flex flex-wrap items-baseline justify-between gap-x-2">
              <span class="text-muted-foreground">
                {report.analysis?.gmi?.value != null ? "GMI (DCCT %)" : "Est. A1c (DCCT %)"}
              </span>
              <span class="text-lg font-semibold tabular-nums whitespace-nowrap">
                {overallStats.a1cDCCT != null
                  ? `${overallStats.a1cDCCT.toFixed(1)}%`
                  : "No estimate"}
              </span>
            </div>
            <ReliabilityBadge reliability={report.analysis?.reliability} />
          </Card.Content>
        </Card.Root>

        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">Glycemic Variability</Card.Title>
            <Card.Description>GVI and PGS metrics</Card.Description>
          </Card.Header>
          <Card.Content>
            <div class="divide-y divide-border">
              <div class="flex flex-wrap items-baseline justify-between gap-x-2 py-2 first:pt-0 last:pb-0">
                <span class="text-muted-foreground">GVI</span>
                <span class="text-lg font-semibold tabular-nums whitespace-nowrap">
                  {overallStats.gvi != null
                    ? overallStats.gvi.toFixed(2)
                    : "No estimate"}
                </span>
              </div>
              <div class="flex flex-wrap items-baseline justify-between gap-x-2 py-2 first:pt-0 last:pb-0">
                <span class="text-muted-foreground">PGS</span>
                <span class="text-lg font-semibold tabular-nums whitespace-nowrap">
                  {overallStats.pgs != null
                    ? overallStats.pgs.toFixed(1)
                    : "No estimate"}
                </span>
              </div>
            </div>
          </Card.Content>
        </Card.Root>

        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">Fluctuation</Card.Title>
            <Card.Description>Daily glucose changes</Card.Description>
          </Card.Header>
          <Card.Content>
            <div class="divide-y divide-border">
              <div class="flex flex-wrap items-baseline justify-between gap-x-2 py-2 first:pt-0 last:pb-0">
                <span class="text-muted-foreground">Mean Total Daily Change</span>
                <span class="text-lg font-semibold tabular-nums whitespace-nowrap">
                  {overallStats.meanTotalDailyChange != null
                    ? `${bg(overallStats.meanTotalDailyChange)} ${bgLabel()}`
                    : "No estimate"}
                </span>
              </div>
              <div class="flex flex-wrap items-baseline justify-between gap-x-2 py-2 first:pt-0 last:pb-0">
                <span class="text-muted-foreground">Time in Fluctuation</span>
                <span class="text-lg font-semibold tabular-nums whitespace-nowrap">
                  {overallStats.timeInFluctuation != null
                    ? `${overallStats.timeInFluctuation.toFixed(1)}%`
                    : "No estimate"}
                </span>
              </div>
            </div>
          </Card.Content>
        </Card.Root>
      </div>
    {/if}
  </div>
{/if}
