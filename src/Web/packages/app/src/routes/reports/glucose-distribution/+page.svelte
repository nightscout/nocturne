<script lang="ts">
  import { Chart, Svg, Arc, Text, Group } from "layerchart";
  import type { Entry } from "$lib/api";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { page } from "$app/state";
  import { getReportsData } from "$lib/data/reports.remote";
  import { getDateRangeInputFromUrl } from "$lib/utils/date-range";
  import { AlertTriangle } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";

  // Build date range input from URL parameters
  const dateRangeInput = $derived(getDateRangeInputFromUrl(page.url));

  // Query for reports data
  const reportsQuery = $derived(getReportsData(dateRangeInput));
  const data = $derived(await reportsQuery);

  // Glucose distribution ranges (matching Nightscout)
  const RANGES = [
    {
      name: "Low",
      min: 0,
      max: DEFAULT_THRESHOLDS.low ?? 70,
      color: "#c30909",
    }, // Red
    {
      name: "In Range",
      min: DEFAULT_THRESHOLDS.low ?? 70,
      max: DEFAULT_THRESHOLDS.high ?? 180,
      color: "#5ab85a", // Green
    },
    {
      name: "High",
      min: DEFAULT_THRESHOLDS.high ?? 180,
      max: Infinity,
      color: "#e9e91a", // Yellow
    },
  ] as const;

  // Calculate statistics for each range
  const rangeStats = $derived.by(() => {
    // Get all valid glucose readings
    const readings = data.entries
      .filter((e: Entry) => e.sgv || e.mgdl)
      .map((e: Entry) => e.sgv ?? e.mgdl ?? 0);

    if (readings.length === 0) {
      return RANGES.map((range) => ({
        name: range.name,
        color: range.color,
        count: 0,
        percentage: 0,
        average: 0,
        median: 0,
        stdDev: 0,
      }));
    }

    return RANGES.map((range) => {
      const inRange = readings.filter((r) => r >= range.min && r < range.max);
      const count = inRange.length;
      const percentage = (count / readings.length) * 100;

      // Calculate stats
      const average =
        count > 0 ? inRange.reduce((a, b) => a + b, 0) / count : 0;

      // Median
      const sorted = [...inRange].sort((a, b) => a - b);
      const median =
        count > 0
          ? count % 2 === 0
            ? (sorted[count / 2 - 1] + sorted[count / 2]) / 2
            : sorted[Math.floor(count / 2)]
          : 0;

      // Standard deviation
      const stdDev =
        count > 0
          ? Math.sqrt(
              inRange.reduce(
                (sum, val) => sum + Math.pow(val - average, 2),
                0
              ) / count
            )
          : 0;

      return {
        name: range.name,
        color: range.color,
        count,
        percentage,
        average,
        median,
        stdDev,
      };
    });
  });

  // Pie chart data
  const pieData = $derived(
    rangeStats.map((stat) => ({
      name: stat.name,
      value: stat.percentage,
      color: stat.color,
    }))
  );

  // Overall statistics
  const overallStats = $derived.by(() => {
    const readings = data.entries
      .filter((e: Entry) => e.sgv || e.mgdl)
      .map((e: Entry) => e.sgv ?? e.mgdl ?? 0);

    if (readings.length === 0) {
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

    const totalReadings = readings.length;
    const mean = readings.reduce((a, b) => a + b, 0) / totalReadings;

    const sorted = [...readings].sort((a, b) => a - b);
    const median =
      totalReadings % 2 === 0
        ? (sorted[totalReadings / 2 - 1] + sorted[totalReadings / 2]) / 2
        : sorted[Math.floor(totalReadings / 2)];

    const stdDev = Math.sqrt(
      readings.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) /
        totalReadings
    );

    // A1c estimation (DCCT formula: A1c = (mean glucose + 46.7) / 28.7)
    const a1cDCCT = (mean + 46.7) / 28.7;

    // A1c (IFCC formula: A1c = 10.929 × (DCCT - 2.15)
    const a1cIFCC = 10.929 * (a1cDCCT - 2.15);

    // Calculate GVI (Glycemic Variability Index)
    // GVI = (L / Lideal) where L is the total length of the glucose curve
    // and Lideal is the length of a straight line between first and last points
    let totalPathLength = 0;
    const sortedByTime = data.entries
      .filter((e: Entry) => (e.sgv || e.mgdl) && e.mills)
      .sort((a: Entry, b: Entry) => (a.mills ?? 0) - (b.mills ?? 0));

    for (let i = 1; i < sortedByTime.length; i++) {
      const prev = sortedByTime[i - 1];
      const curr = sortedByTime[i];
      const dTime = ((curr.mills ?? 0) - (prev.mills ?? 0)) / (5 * 60 * 1000); // Normalize to 5-min intervals
      const dGlucose =
        (curr.sgv ?? curr.mgdl ?? 0) - (prev.sgv ?? prev.mgdl ?? 0);
      totalPathLength += Math.sqrt(dTime * dTime + dGlucose * dGlucose);
    }

    const firstReading = sortedByTime[0];
    const lastReading = sortedByTime[sortedByTime.length - 1];
    const idealLength =
      sortedByTime.length > 1
        ? Math.sqrt(
            Math.pow(
              ((lastReading.mills ?? 0) - (firstReading.mills ?? 0)) /
                (5 * 60 * 1000),
              2
            ) +
              Math.pow(
                (lastReading.sgv ?? lastReading.mgdl ?? 0) -
                  (firstReading.sgv ?? firstReading.mgdl ?? 0),
                2
              )
          )
        : 1;

    const gvi = idealLength > 0 ? totalPathLength / idealLength : 0;

    // PGS (Patient Glycemic Status) = GVI × mean glucose × (1 - % time in range / 100)
    const tir = rangeStats.find((s) => s.name === "In Range")?.percentage ?? 0;
    const pgs = gvi * mean * (1 - tir / 100);

    // Mean Total Daily Change
    let totalChange = 0;
    for (let i = 1; i < sortedByTime.length; i++) {
      const prev = sortedByTime[i - 1];
      const curr = sortedByTime[i];
      totalChange += Math.abs(
        (curr.sgv ?? curr.mgdl ?? 0) - (prev.sgv ?? prev.mgdl ?? 0)
      );
    }

    // Calculate number of days in dataset
    const firstTime = firstReading?.mills ?? Date.now();
    const lastTime = lastReading?.mills ?? Date.now();
    const numDays = Math.max(1, (lastTime - firstTime) / (24 * 60 * 60 * 1000));
    const meanTotalDailyChange = totalChange / numDays;

    // Time in fluctuation (readings where change from previous > 15 mg/dL in 5 min)
    let fluctuationCount = 0;
    for (let i = 1; i < sortedByTime.length; i++) {
      const prev = sortedByTime[i - 1];
      const curr = sortedByTime[i];
      const timeDiff = (curr.mills ?? 0) - (prev.mills ?? 0);
      if (timeDiff <= 6 * 60 * 1000) {
        // Within ~5-6 minutes
        const glucoseDiff = Math.abs(
          (curr.sgv ?? curr.mgdl ?? 0) - (prev.sgv ?? prev.mgdl ?? 0)
        );
        if (glucoseDiff > 15) {
          fluctuationCount++;
        }
      }
    }
    const timeInFluctuation = (fluctuationCount / totalReadings) * 100;

    return {
      totalReadings,
      mean,
      median,
      stdDev,
      a1cDCCT,
      a1cIFCC,
      gvi,
      pgs,
      meanTotalDailyChange,
      timeInFluctuation,
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

<svelte:boundary>
  {#snippet pending()}
    <div class="space-y-6 p-4">
      <div class="flex items-center justify-center h-64">
        <div class="text-center space-y-4">
          <div
            class="animate-spin rounded-full h-8 w-8 border-b-2 border-primary mx-auto"
          ></div>
          <p class="text-muted-foreground">Loading glucose distribution...</p>
        </div>
      </div>
    </div>
  {/snippet}

  {#snippet failed(error)}
    <div class="space-y-6 p-4">
      <Card.Root class="border-2 border-destructive">
        <Card.Header>
          <Card.Title class="flex items-center gap-2 text-destructive">
            <AlertTriangle class="w-5 h-5" />
            Error Loading Data
          </Card.Title>
        </Card.Header>
        <Card.Content>
          <p class="text-destructive-foreground">{error.message}</p>
          <Button
            variant="outline"
            class="mt-4"
            onclick={() => getReportsData(dateRangeInput).refresh()}
          >
            Try again
          </Button>
        </Card.Content>
      </Card.Root>
    </div>
  {/snippet}

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
              {@const totalValue = pieData.reduce((sum, d) => sum + d.value, 0)}
              {@const arcsData = pieData.reduce((acc, d, i) => {
                const startAngle = i === 0 ? 0 : acc[i - 1].endAngle;
                const endAngle =
                  startAngle + (d.value / totalValue) * Math.PI * 2;
                acc.push({ ...d, startAngle, endAngle });
                return acc;
              }, [] as Array)}
              <div class="h-[300px] w-[300px]">
                <Chart>
                  <Svg>
                    <Group center>
                      {#each arcsData as arc}
                        <Arc
                          startAngle={arc.startAngle}
                          endAngle={arc.endAngle}
                          innerRadius={60}
                          outerRadius={120}
                          fill={arc.color}
                        />
                        <!-- Label -->
                        {#if arc.value > 5}
                          {@const midAngle =
                            (arc.startAngle + arc.endAngle) / 2}
                          {@const labelRadius = 90}
                          {@const x = Math.sin(midAngle) * labelRadius}
                          {@const y = -Math.cos(midAngle) * labelRadius}
                          <Text
                            {x}
                            {y}
                            textAnchor="middle"
                            verticalAnchor="middle"
                            class="fill-foreground text-sm font-medium"
                          >
                            {arc.value.toFixed(1)}%
                          </Text>
                        {/if}
                      {/each}
                      <!-- Center text -->
                      <Text
                        x={0}
                        y={-10}
                        textAnchor="middle"
                        class="fill-foreground text-2xl font-bold"
                      >
                        {rangeStats
                          .find((s) => s.name === "In Range")
                          ?.percentage.toFixed(0)}%
                      </Text>
                      <Text
                        x={0}
                        y={15}
                        textAnchor="middle"
                        class="fill-muted-foreground text-xs"
                      >
                        In Range
                      </Text>
                    </Group>
                  </Svg>
                </Chart>
              </div>
            {:else}
              <div
                class="flex h-[300px] items-center justify-center text-muted-foreground"
              >
                No data available
              </div>
            {/if}

            <!-- Legend -->
            <div class="mt-4 flex justify-center gap-6">
              {#each rangeStats as stat}
                <div class="flex items-center gap-2">
                  <div
                    class="h-4 w-4 rounded"
                    style="background-color: {stat.color}"
                  ></div>
                  <span class="text-sm">{stat.name}</span>
                </div>
              {/each}
            </div>
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
                <Table.Head class="text-right">%</Table.Head>
                <Table.Head class="text-right">Count</Table.Head>
                <Table.Head class="text-right">Average</Table.Head>
                <Table.Head class="text-right">Median</Table.Head>
                <Table.Head class="text-right">Std Dev</Table.Head>
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
                  <Table.Cell class="text-right">{stat.count}</Table.Cell>
                  <Table.Cell class="text-right">
                    {stat.average > 0 ? stat.average.toFixed(0) : "—"}
                  </Table.Cell>
                  <Table.Cell class="text-right">
                    {stat.median > 0 ? stat.median.toFixed(0) : "—"}
                  </Table.Cell>
                  <Table.Cell class="text-right">
                    {stat.stdDev > 0 ? stat.stdDev.toFixed(1) : "—"}
                  </Table.Cell>
                </Table.Row>
              {/each}
            </Table.Body>
          </Table.Root>
        </Card.Content>
      </Card.Root>
    </div>

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
</svelte:boundary>
