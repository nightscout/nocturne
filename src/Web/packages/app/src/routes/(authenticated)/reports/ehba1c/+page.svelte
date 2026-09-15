<script lang="ts">
  import { onMount } from "svelte";
  import { LineChart } from "layerchart";
  import { Loader2, Activity } from "lucide-svelte";
  import * as Card from "$lib/components/ui/card";
  import {
    getAvailableYears,
    getEHbA1cTimeline,
  } from "$api/generated/dataOverviews.generated.remote";
  import type { EHbA1cPoint } from "$api/generated/nocturne-api-client";
  import { bg, bgLabel, formatLongDate } from "$lib/utils/formatting";

  type ChartPoint = {
    date: Date;
    estimatedA1cPercent: number;
    weightedAverageGlucoseMgdl: number;
    readingCount: number;
    daysWithData: number;
  };

  let loading = $state(true);
  let error = $state<unknown>(null);
  let pointsByYear = $state<Map<number, EHbA1cPoint[]>>(new Map());

  function toChartPoints(pointsMap: Map<number, EHbA1cPoint[]>): ChartPoint[] {
    const all: ChartPoint[] = [];
    for (const points of pointsMap.values()) {
      for (const point of points) {
        const [y, m, d] = (point.date ?? "").split("-").map(Number);
        if (!y || !m || !d) continue;
        all.push({
          date: new Date(y, m - 1, d),
          estimatedA1cPercent: point.estimatedA1cPercent ?? 0,
          weightedAverageGlucoseMgdl: point.weightedAverageGlucoseMgdl ?? 0,
          readingCount: point.readingCount ?? 0,
          daysWithData: point.daysWithData ?? 0,
        });
      }
    }
    return all.sort((a, b) => a.date.getTime() - b.date.getTime());
  }

  const chartData = $derived(toChartPoints(pointsByYear));

  const latest = $derived(chartData.length > 0 ? chartData[chartData.length - 1] : undefined);

  async function loadAll() {
    loading = true;
    error = null;
    try {
      const { years } = await getAvailableYears().run();
      const results = await Promise.all(
        (years ?? []).map(async (year) => {
          const response = await getEHbA1cTimeline({ year }).run();
          return [year, response.points ?? []] as const;
        })
      );
      pointsByYear = new Map(results);
    } catch (err) {
      error = err;
      console.error("Failed to load eHbA1c timeline:", err);
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    queueMicrotask(loadAll);
  });
</script>

<div class="@container space-y-6 p-3 @md:p-6">
  <Card.Root>
    <Card.Header>
      <Card.Title class="flex items-center gap-2">
        <Activity class="h-5 w-5 text-muted-foreground" />
        Estimated HbA1c (eHbA1c)
      </Card.Title>
      <Card.Description>
        A day-by-day estimate of what a lab HbA1c would read, based on a recency-weighted
        average of your trailing 90-day glucose readings — the most recent 30 days count for
        roughly half the estimate, tapering off smoothly for older days, the way glycated
        hemoglobin actually reflects glucose exposure over time.
      </Card.Description>
    </Card.Header>
    <Card.Content>
      {#if loading}
        <div class="flex h-[320px] items-center justify-center text-muted-foreground">
          <Loader2 class="h-5 w-5 animate-spin mr-2" /> Loading eHbA1c timeline...
        </div>
      {:else if error}
        <div class="flex h-[320px] items-center justify-center text-destructive">
          Failed to load eHbA1c data. Please try again later.
        </div>
      {:else if chartData.length === 0}
        <div class="flex h-[320px] items-center justify-center text-muted-foreground">
          Not enough glucose history yet — each point needs at least a month of readings within
          the trailing 90 days.
        </div>
      {:else}
        {#if latest}
          <div class="mb-4 flex flex-wrap items-baseline gap-x-6 gap-y-1">
            <div>
              <span class="text-3xl font-semibold">{latest.estimatedA1cPercent.toFixed(1)}%</span>
              <span class="ml-2 text-sm text-muted-foreground">
                latest estimate ({formatLongDate(latest.date)})
              </span>
            </div>
            <div class="text-sm text-muted-foreground">
              Weighted mean glucose: {bg(latest.weightedAverageGlucoseMgdl)} {bgLabel()}
            </div>
          </div>
        {/if}

        <div class="h-[320px] w-full @md:h-[400px]">
          <LineChart
            data={chartData}
            x="date"
            y="estimatedA1cPercent"
            series={[
              {
                key: "estimatedA1cPercent",
                label: "eHbA1c %",
                color: "var(--chart-1)",
              },
            ]}
          />
        </div>
      {/if}
    </Card.Content>
  </Card.Root>
</div>
