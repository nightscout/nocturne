<script lang="ts">
  import { PieChart, Text } from "layerchart";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import Button from "$lib/components/ui/button/button.svelte";
  import { getReportsData } from "$api/reports.remote";
  import HourlyGlucoseDistributionChart from "$lib/components/reports/HourlyGlucoseDistributionChart.svelte";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  const reportsParams = requireDateParamsContext(14);

  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Glucose Distribution" }
  );

  let showTightRange = $state(true);

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

  const overallStats = $derived.by(() => {
    const analysis = reportsResource.current?.analysis;
    const basicStats = analysis?.basicStats;
    const glycemicVariability = analysis?.glycemicVariability;

    if (!basicStats || basicStats.count === 0) {
      return {
        totalReadings: 0,
        mean: 0,
        median: 0,
        stdDev: 0,
        a1cDCCT: 0,
        a1cIFCC: 0,
        gvi: 0,
        pgs: 0,
        meanTotalDailyChange: 0,
        timeInFluctuation: 0,
      };
    }

    const a1cDCCT = analysis?.gmi?.value ?? glycemicVariability?.estimatedA1c ?? 0;
    const a1cIFCC = 10.929 * (a1cDCCT - 2.15);

    return {
      totalReadings: basicStats.count ?? 0,
      mean: basicStats.mean ?? 0,
      median: basicStats.median ?? 0,
      stdDev: basicStats.standardDeviation ?? 0,
      a1cDCCT,
      a1cIFCC,
      gvi: glycemicVariability?.glycemicVariabilityIndex ?? 0,
      pgs: glycemicVariability?.patientGlycemicStatus ?? 0,
      meanTotalDailyChange: glycemicVariability?.meanTotalDailyChange ?? 0,
      timeInFluctuation: glycemicVariability?.timeInFluctuation ?? 0,
    };
  });

  const dateRangeDisplay = $derived.by(() => {
    const dateRange = reportsResource.current?.dateRange;
    if (!dateRange) return "";
    const options: Intl.DateTimeFormatOptions = { month: "short", day: "numeric", year: "numeric" };
    return `${new Date(dateRange.from).toLocaleDateString(undefined, options)} – ${new Date(dateRange.to).toLocaleDateString(undefined, options)}`;
  });
</script>

{#if reportsResource.current}
  {@const report = reportsResource.current}
  <div class="space-y-6 p-4">
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

    <div class="grid gap-6 lg:grid-cols-2">
      <Card.Root>
        <Card.Header>
          <div class="flex items-center justify-between">
            <Card.Title class="text-lg">Distribution Chart</Card.Title>
            <Button
              variant="ghost"
              size="sm"
              onclick={() => (showTightRange = !showTightRange)}
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

    <div class="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
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
                {overallStats.a1cDCCT.toFixed(1)}%
              </span>
            </div>
            <div class="flex justify-between">
              <span class="text-muted-foreground">A1c (IFCC)</span>
              <span class="text-2xl font-bold">
                {overallStats.a1cIFCC.toFixed(0)} mmol/mol
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
                {overallStats.meanTotalDailyChange.toFixed(0)} mg/dL
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
        <div class="grid gap-4 md:grid-cols-4">
          <div class="text-center">
            <div class="text-3xl font-bold">
              {overallStats.mean.toFixed(0)}
            </div>
            <div class="text-sm text-muted-foreground">Mean (mg/dL)</div>
          </div>
          <div class="text-center">
            <div class="text-3xl font-bold">
              {overallStats.median.toFixed(0)}
            </div>
            <div class="text-sm text-muted-foreground">Median (mg/dL)</div>
          </div>
          <div class="text-center">
            <div class="text-3xl font-bold">
              {overallStats.stdDev.toFixed(1)}
            </div>
            <div class="text-sm text-muted-foreground">Std Dev</div>
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
  </div>
{/if}
