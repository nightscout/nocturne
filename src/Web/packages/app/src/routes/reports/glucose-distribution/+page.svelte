<script lang="ts">
  import { PieChart, Text } from "layerchart";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { getReportsData } from "$lib/data/reports.remote";
  import { AlertTriangle } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import HourlyGlucoseDistributionChart from "$lib/components/reports/HourlyGlucoseDistributionChart.svelte";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { resource } from "runed";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is standard for glucose distribution analysis
  const reportsParams = requireDateParamsContext(14);

  // Use resource for controlled reactivity - prevents excessive re-fetches
  const reportsResource = resource(
    () => reportsParams.dateRangeInput,
    async (dateRangeInput) => {
      return await getReportsData(dateRangeInput);
    },
    { debounce: 100 }
  );

  // Loading state
  const isLoading = $derived(reportsResource.loading);

  // Unwrap the data from the resource with null safety
  const data = $derived({
    entries: reportsResource.current?.entries ?? [],
    treatments: reportsResource.current?.treatments ?? [],
    analysis: reportsResource.current?.analysis,
    averagedStats: reportsResource.current?.averagedStats,
    dateRange: reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
      lastUpdated: new Date().toISOString(),
    },
  });

  // Glucose distribution ranges (using CSS variables for theme support)
  const RANGES = [
    { name: "Very Low", color: "#8b5cf6" }, // purple - <54 mg/dL
    { name: "Low", color: "var(--glucose-low)" }, // red - 54-70 mg/dL
    { name: "Tight Range", color: "#22c55e" }, // bright green - 70-140 mg/dL
    { name: "In Range", color: "#16a34a" }, // darker green - 70-180 mg/dL
    { name: "High", color: "var(--glucose-high)" }, // yellow - 180-250 mg/dL
    { name: "Very High", color: "#ea580c" }, // dark orange - >250 mg/dL
  ] as const;

  const rangeStats = $derived.by(() => {
    const analysis = reportsResource.current?.analysis;
    const tir = analysis?.timeInRange?.percentages;

    if (!tir) {
      return RANGES.map((range) => ({
        name: range.name,
        color: range.color,
        percentage: 0,
      }));
    }

    return [
      {
        name: "Very Low",
        color: RANGES[0].color,
        percentage: tir.severeLow ?? 0,
      },
      {
        name: "Low",
        color: RANGES[1].color,
        percentage: tir.low ?? 0,
      },
      {
        name: "Tight Range",
        color: RANGES[2].color,
        percentage: tir.tightTarget ?? 0,
      },
      {
        name: "In Range",
        color: RANGES[3].color,
        percentage: tir.target ?? 0,
      },
      {
        name: "High",
        color: RANGES[4].color,
        percentage: tir.high ?? 0,
      },
      {
        name: "Very High",
        color: RANGES[5].color,
        percentage: tir.severeHigh ?? 0,
      },
    ];
  });

  // Pie chart data
  const pieData = $derived(
    rangeStats.map((stat) => ({
      name: stat.name,
      value: stat.percentage,
      color: stat.color,
    }))
  );

  // Overall statistics from backend
  const overallStats = $derived.by(() => {
    const analysis = data.analysis;
    const basicStats = analysis?.basicStats;
    const glycemicVariability = analysis?.glycemicVariability;
    const gmi = analysis?.gmi;

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

    // GMI value is the A1c estimate (DCCT format)
    const a1cDCCT = gmi?.value ?? glycemicVariability?.estimatedA1c ?? 0;
    // Convert DCCT to IFCC: IFCC = 10.929 × (DCCT - 2.15)
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

  // Date range display
  const dateRangeDisplay = $derived.by(() => {
    const from = new Date(data.dateRange.from);
    const to = new Date(data.dateRange.to);
    const options: Intl.DateTimeFormatOptions = {
      month: "short",
      day: "numeric",
      year: "numeric",
    };
    return `${from.toLocaleDateString(undefined, options)} – ${to.toLocaleDateString(undefined, options)}`;
  });
</script>

{#if isLoading && !reportsResource.current}
  <ReportsSkeleton />
{:else if reportsResource.error}
  <div class="space-y-6 p-4">
    <Card.Root class="border-2 border-destructive">
      <Card.Header>
        <Card.Title class="flex items-center gap-2 text-destructive">
          <AlertTriangle class="w-5 h-5" />
          Error Loading Data
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <p class="text-destructive-foreground">
          {reportsResource.error instanceof Error ? reportsResource.error.message : String(reportsResource.error)}
        </p>
        <Button
          variant="outline"
          class="mt-4"
          onclick={() => reportsResource.refetch()}
        >
          Try again
        </Button>
      </Card.Content>
    </Card.Root>
  </div>
{:else}
  <div class="space-y-6 p-4">
    <!-- Header -->
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
      <!-- Pie Chart -->
      <Card.Root>
        <Card.Header>
          <Card.Title class="text-lg">Distribution Chart</Card.Title>
        </Card.Header>
        <Card.Content>
          <div class="flex flex-col items-center">
            {#if pieData.some((d) => d.value > 0)}
              <div class="h-[300px] w-full">
                <PieChart
                  data={rangeStats}
                  key="name"
                  value="percentage"
                  cRange={rangeStats.map((s) => s.color)}
                  innerRadius={-60}
                  cornerRadius={3}
                  padAngle={0.02}
                  renderContext="svg"
                  legend
                  props={{
                    legend: {
                      placement: "bottom",
                    },
                  }}
                >
                  {#snippet aboveMarks()}
                    <Text
                      value={`${rangeStats.find((s) => s.name === "In Range")?.percentage.toFixed(0)}%`}
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

      <!-- Statistics Table -->
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
                      {stat.name}
                    </div>
                  </Table.Cell>
                  <Table.Cell class="text-right font-medium">
                    {stat.percentage.toFixed(1)}%
                  </Table.Cell>
                </Table.Row>
              {/each}
            </Table.Body>
          </Table.Root>
        </Card.Content>
      </Card.Root>
    </div>

    <!-- Hourly Distribution Chart -->
    <Card.Root>
      <Card.Header>
        <Card.Title class="text-lg">Hourly Distribution</Card.Title>
        <Card.Description>
          Percentage of time in each glucose range by hour of day
        </Card.Description>
      </Card.Header>
      <Card.Content>
        <HourlyGlucoseDistributionChart averagedStats={data.averagedStats} />
      </Card.Content>
    </Card.Root>

    <!-- Additional Statistics -->
    <div class="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
      <!-- A1c Estimation -->
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
        </Card.Content>
      </Card.Root>

      <!-- Glycemic Variability -->
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

      <!-- Daily Fluctuation -->
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

    <!-- Overall Summary -->
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
