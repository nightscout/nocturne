<script lang="ts">
  import { onMount } from "svelte";
  import { LineChart } from "layerchart";
  import { Loader2, Activity } from "lucide-svelte";
  import * as Card from "$lib/components/ui/card";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
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

  type A1cUnit = "percent" | "mmol";

  /**
   * No lab reference range for a non-diabetic adult goes below this, so the chart neither
   * draws nor colors that region — it would only be empty space.
   */
  const MIN_A1C_PERCENT = 4.0;

  /**
   * Reference zones for the legend/bands, in DCCT/NGSP %. Bounds are the widely-cited ADA
   * thresholds (normal < 5.7%, prediabetes 5.7–6.4%, diabetes ≥ 6.5%); "Doel Type 1 diabetes"
   * uses the general ADA adult target of < 7.0%, and "Te hoog"/"Gevaarlijk hoog" split the
   * range above that at 9.0%, where complication risk rises sharply. Swatches reuse the GRI
   * report's green/yellow-green/orange/red severity scale; band fills use dedicated,
   * dark-mode-tuned tokens (the swatch colors are too subtle at chart-fill opacity, and
   * --glucose-in-range/--chart-2 turned out to be the same color when tried here).
   */
  const A1C_ZONES: { key: string; label: string; maxPercent: number; swatch: string; fill: string }[] = [
    { key: "healthy", label: "Gezond persoon range", maxPercent: 5.7, swatch: "var(--gri-zone-a)", fill: "var(--ehba1c-zone-healthy)" },
    { key: "target", label: "Doel Type 1 diabetes", maxPercent: 7.0, swatch: "var(--gri-zone-b)", fill: "var(--ehba1c-zone-target)" },
    { key: "high", label: "Te hoog", maxPercent: 9.0, swatch: "var(--gri-zone-d)", fill: "var(--ehba1c-zone-high)" },
    { key: "veryHigh", label: "Gevaarlijk hoog", maxPercent: 14.0, swatch: "var(--gri-zone-e)", fill: "var(--ehba1c-zone-very-high)" },
  ];

  let loading = $state(true);
  let error = $state<unknown>(null);
  let pointsByYear = $state<Map<number, EHbA1cPoint[]>>(new Map());
  let a1cUnit = $state<A1cUnit>("percent");

  /** NGSP % to IFCC mmol/mol, the standard dual-reporting conversion for HbA1c. */
  function toIfccMmolMol(percent: number): number {
    return (percent - 2.15) * 10.929;
  }

  function toDisplayUnit(percent: number): number {
    return a1cUnit === "percent" ? percent : toIfccMmolMol(percent);
  }

  function formatA1c(percent: number): string {
    return a1cUnit === "percent"
      ? `${percent.toFixed(1)}%`
      : `${Math.round(toIfccMmolMol(percent))} mmol/mol`;
  }

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

  const displayChartData = $derived(
    chartData.map((p) => ({ ...p, displayValue: toDisplayUnit(p.estimatedA1cPercent) }))
  );

  const latest = $derived(chartData.length > 0 ? chartData[chartData.length - 1] : undefined);

  const extremes = $derived.by(() => {
    if (chartData.length === 0) return undefined;
    let highest = chartData[0];
    let lowest = chartData[0];
    for (const p of chartData) {
      if (p.estimatedA1cPercent > highest.estimatedA1cPercent) highest = p;
      if (p.estimatedA1cPercent < lowest.estimatedA1cPercent) lowest = p;
    }
    return { highest, lowest };
  });

  const zoneBands = $derived(
    A1C_ZONES.map((zone, i) => {
      const minPercent = i === 0 ? MIN_A1C_PERCENT : A1C_ZONES[i - 1].maxPercent;
      return {
        ...zone,
        minPercent,
        isLast: i === A1C_ZONES.length - 1,
        yMin: toDisplayUnit(minPercent),
        yMax: toDisplayUnit(zone.maxPercent),
      };
    })
  );

  function formatZoneRange(band: (typeof zoneBands)[number]): string {
    const unit = a1cUnit === "percent" ? "%" : " mmol/mol";
    const fmt = (p: number) => (a1cUnit === "percent" ? p.toFixed(1) : `${Math.round(toIfccMmolMol(p))}`);
    if (band.isLast) return `> ${fmt(band.minPercent)}${unit}`;
    return `${fmt(band.minPercent)}–${fmt(band.maxPercent)}${unit}`;
  }

  const annotations = $derived(
    zoneBands.map((band) => ({
      type: "range" as const,
      layer: "below" as const,
      y: [band.yMin, band.yMax] as [number, number],
      fill: band.fill,
    }))
  );

  /** Fixed floor at the never-goes-lower bound; auto-scaled ceiling with a little headroom. */
  const yDomain = $derived.by((): [number, number] => {
    const dataMaxPercent =
      chartData.length > 0
        ? Math.max(...chartData.map((p) => p.estimatedA1cPercent))
        : A1C_ZONES[1].maxPercent;
    const maxPercent = Math.max(dataMaxPercent + 0.5, A1C_ZONES[1].maxPercent);
    return [toDisplayUnit(MIN_A1C_PERCENT), toDisplayUnit(maxPercent)];
  });

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
    <Card.Header class="flex flex-row flex-wrap items-start justify-between gap-4">
      <div>
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
      </div>
      <ToggleGroup.Root
        type="single"
        value={a1cUnit}
        onValueChange={(next: string) => {
          if (next === "percent" || next === "mmol") a1cUnit = next;
        }}
        class="shrink-0 rounded-md border bg-background p-0.5"
      >
        <ToggleGroup.Item value="percent" class="h-8 px-3 text-xs" aria-label="Toon in procent">
          %
        </ToggleGroup.Item>
        <ToggleGroup.Item value="mmol" class="h-8 px-3 text-xs" aria-label="Toon in mmol/mol">
          mmol/mol
        </ToggleGroup.Item>
      </ToggleGroup.Root>
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
        {#if latest && extremes}
          <div class="mb-4 flex flex-wrap items-baseline gap-x-6 gap-y-1">
            <div>
              <span class="text-3xl font-semibold">{formatA1c(latest.estimatedA1cPercent)}</span>
              <span class="ml-2 text-sm text-muted-foreground">
                latest estimate ({formatLongDate(latest.date)})
              </span>
            </div>
            <div class="text-sm text-muted-foreground">
              Weighted mean glucose: {bg(latest.weightedAverageGlucoseMgdl)} {bgLabel()}
            </div>
            <div class="text-sm text-muted-foreground">
              Highest: <span class="font-medium text-foreground">{formatA1c(extremes.highest.estimatedA1cPercent)}</span>
              ({formatLongDate(extremes.highest.date)})
            </div>
            <div class="text-sm text-muted-foreground">
              Lowest: <span class="font-medium text-foreground">{formatA1c(extremes.lowest.estimatedA1cPercent)}</span>
              ({formatLongDate(extremes.lowest.date)})
            </div>
          </div>
        {/if}

        <div class="h-[320px] w-full @md:h-[400px]">
          <LineChart
            data={displayChartData}
            x="date"
            y="displayValue"
            {yDomain}
            series={[
              {
                key: "displayValue",
                label: a1cUnit === "percent" ? "eHbA1c %" : "eHbA1c mmol/mol",
                color: "var(--chart-1)",
              },
            ]}
            {annotations}
          />
        </div>

        <div class="mt-4 flex flex-wrap gap-x-5 gap-y-2 text-sm">
          {#each zoneBands as band (band.key)}
            <div class="flex items-center gap-1.5">
              <span class="h-2.5 w-2.5 rounded-full" style="background-color: {band.swatch}"></span>
              <span>{band.label}</span>
              <span class="text-muted-foreground">({formatZoneRange(band)})</span>
            </div>
          {/each}
        </div>
      {/if}
    </Card.Content>
  </Card.Root>
</div>
