<script lang="ts">
  import { PieChart, Text } from "layerchart";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { Button } from "$lib/components/ui/button";
  import { getReportsAnalysis } from "$api/reports.remote";
  import HourlyGlucoseDistributionChart from "$lib/components/reports/HourlyGlucoseDistributionChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
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

    const stats = [
      { key: "Very Low", color: "var(--glucose-very-low)", value: tir?.veryLow ?? 0 },
      { key: "Low", color: "var(--glucose-low)", value: tir?.low ?? 0 },
    ];

    if (showTightRange) {
      stats.push(
        { key: "Tight Range", color: "var(--glucose-tight-range)", value: tir?.tightTarget ?? 0 },
        { key: "In Range", color: "var(--glucose-in-range)", value: (tir?.target ?? 0) - (tir?.tightTarget ?? 0) },
      );
    } else {
      stats.push(
        { key: "In Range", color: "var(--glucose-in-range)", value: tir?.target ?? 0 },
      );
    }

    stats.push(
      { key: "High", color: "var(--glucose-high)", value: tir?.high ?? 0 },
      { key: "Very High", color: "var(--glucose-very-high)", value: tir?.veryHigh ?? 0 },
    );

    return stats;
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
      gvi: glycemicVariability?.glycemicVariabilityIndex ?? 0,
      pgs: glycemicVariability?.patientGlycemicStatus ?? 0,
      meanTotalDailyChange: glycemicVariability?.meanTotalDailyChange ?? 0,
      timeInFluctuation: glycemicVariability?.timeInFluctuation ?? 0,
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
    <Card.Root>
      <Card.Header>
        <Card.Title class="flex items-center gap-2">
          Glucose Distribution
        </Card.Title>
        <Card.Description>
          {dateRangeDisplay} • {overallStats.totalReadings} readings
        </Card.Description>
      </Card.Header>
    </Card.Root>

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
      <div class="grid gap-6 @3xl:grid-cols-2">
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
                    legend
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
                  {#each rangeStats as stat}
                    <Table.Row>
                      <Table.Cell>
                        <div class="flex items-center gap-2">
                          <div
                            class="h-3 w-3 rounded-full"
                            style="background-color: {stat.color}"
                          ></div>
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

      <div class="grid gap-6 @2xl:grid-cols-2 @4xl:grid-cols-3">
        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">A1c Estimation</Card.Title>
            <Card.Description>Based on average glucose</Card.Description>
          </Card.Header>
          <Card.Content>
            <div class="space-y-4">
              <div class="flex justify-between">
                <span class="text-muted-foreground">A1c (DCCT)</span>
                <span class="text-2xl font-bold">
                  {overallStats.a1cDCCT != null
                    ? `${overallStats.a1cDCCT.toFixed(1)}%`
                    : "No estimate"}
                </span>
              </div>
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
            <div class="space-y-4">
              <div class="flex justify-between">
                <span class="text-muted-foreground">GVI</span>
                <span class="text-2xl font-bold">
                  {overallStats.gvi.toFixed(2)}
                </span>
              </div>
              <div class="flex justify-between">
                <span class="text-muted-foreground">PGS</span>
                <span class="text-2xl font-bold">
                  {overallStats.pgs.toFixed(1)}
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
            <div class="space-y-4">
              <div class="flex justify-between">
                <span class="text-muted-foreground">Mean Total Daily Change</span>
                <span class="text-2xl font-bold">
                  {bg(overallStats.meanTotalDailyChange)} {bgLabel()}
                </span>
              </div>
              <div class="flex justify-between">
                <span class="text-muted-foreground">Time in Fluctuation</span>
                <span class="text-2xl font-bold">
                  {overallStats.timeInFluctuation.toFixed(1)}%
                </span>
              </div>
            </div>
          </Card.Content>
        </Card.Root>
      </div>

      <Card.Root>
        <Card.Header>
          <Card.Title class="text-lg">Overall Summary</Card.Title>
        </Card.Header>
        <Card.Content>
          <div class="grid gap-4 grid-cols-2 @4xl:grid-cols-4">
            <div class="text-center">
              <div class="text-3xl font-bold">
                {bg(overallStats.mean)}
              </div>
              <div class="text-sm text-muted-foreground">Mean ({bgLabel()})</div>
            </div>
            <div class="text-center">
              <div class="text-3xl font-bold">
                {bg(overallStats.median)}
              </div>
              <div class="text-sm text-muted-foreground">Median ({bgLabel()})</div>
            </div>
            <div class="text-center">
              <div class="text-3xl font-bold">
                {bg(overallStats.stdDev)}
              </div>
              <div class="text-sm text-muted-foreground">Std Dev ({bgLabel()})</div>
            </div>
            <div class="text-center">
              <div class="text-3xl font-bold">
                {overallStats.totalReadings}
              </div>
              <div class="text-sm text-muted-foreground">Readings</div>
            </div>
          </div>
        </Card.Content>
      </Card.Root>
    {/if}
  </div>
{/if}
