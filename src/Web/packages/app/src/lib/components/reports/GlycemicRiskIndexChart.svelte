<script lang="ts">
  import { Chart, Svg, Axis, Polygon, Points, Legend, Tooltip } from "layerchart";
  import { scaleLinear, scaleOrdinal } from "d3-scale";
  import type { GlycemicRiskIndex, GriTimelinePeriod } from "$lib/api/generated/nocturne-api-client";
  import { formatGlucoseValue, getUnitLabel } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";

  interface Props {
    gri: GlycemicRiskIndex;
    timeSeriesData?: GriTimelinePeriod[];
  }

  let { gri, timeSeriesData }: Props = $props();

  const HYPO_MAX = 20;
  const HYPER_MAX = 40;

  // Determine if we're in time-series mode (more than 1 data point)
  const isTimeSeries = $derived(
    timeSeriesData != null && timeSeriesData.length > 1
  );

  // Derive sorted time-series points with computed grayscale fills
  const timeSeriesPoints = $derived.by(() => {
    if (!isTimeSeries || !timeSeriesData) return [];

    const sorted = [...timeSeriesData].sort((a, b) => {
      const aTime = a.periodStart ? new Date(a.periodStart).getTime() : 0;
      const bTime = b.periodStart ? new Date(b.periodStart).getTime() : 0;
      return aTime - bTime;
    });

    const n = sorted.length;

    return sorted.map((period, i) => {
      const t = i / (n - 1);
      const gray = Math.round(t * 255);
      const fill = `rgb(${gray}, ${gray}, ${gray})`;
      const stroke = gray > 200 ? "hsl(var(--border))" : fill;

      return {
        ...period,
        hypo: period.gri?.hypoglycemiaComponent ?? 0,
        hyper: period.gri?.hyperglycemiaComponent ?? 0,
        fill,
        stroke,
      };
    });
  });

  // Format period start date as "Month Year"
  function formatPeriodLabel(periodStart?: string): string {
    if (!periodStart) return "";
    const date = new Date(periodStart);
    return date.toLocaleDateString(undefined, { month: "long", year: "numeric" });
  }

  // Zone boundaries: diagonal lines where hypo + hyper = GRI score threshold
  // Raw GRI thresholds are used; areas beyond the max visible GRI (HYPO_MAX + HYPER_MAX) are unzoned
  const zones = [
    {
      label: "E",
      color: "var(--gri-zone-e)",
      vertices: buildZonePolygon(40, 50),
    },
    {
      label: "D",
      color: "var(--gri-zone-d)",
      vertices: buildZonePolygon(30, 40),
    },
    {
      label: "C",
      color: "var(--gri-zone-c)",
      vertices: buildZonePolygon(20, 30),
    },
    {
      label: "B",
      color: "var(--gri-zone-b)",
      vertices: buildZonePolygon(10, 20),
    },
    {
      label: "A",
      color: "var(--gri-zone-a)",
      vertices: buildZonePolygon(0, 10),
    },
  ];

  function buildZonePolygon(
    lower: number,
    upper: number
  ): { x: number; y: number }[] {
    const corners = [
      { x: 0, y: 0 },
      { x: HYPO_MAX, y: 0 },
      { x: HYPO_MAX, y: HYPER_MAX },
      { x: 0, y: HYPER_MAX },
    ];

    const edges: [{ x: number; y: number }, { x: number; y: number }][] = [
      [corners[0], corners[1]],
      [corners[1], corners[2]],
      [corners[2], corners[3]],
      [corners[3], corners[0]],
    ];

    const allPoints: { x: number; y: number }[] = [];

    // Include rectangle corners that fall within this zone
    for (const corner of corners) {
      const sum = corner.x + corner.y;
      if (sum >= lower && sum <= upper) {
        allPoints.push(corner);
      }
    }

    // Include intersection points of zone diagonals with rectangle edges
    for (const [a, b] of edges) {
      for (const threshold of [lower, upper]) {
        const pt = intersectSegmentWithDiagonal(a, b, threshold);
        if (pt) allPoints.push(pt);
      }
    }

    if (allPoints.length < 3) return allPoints;

    // Sort by angle from centroid to form a convex polygon
    const cx = allPoints.reduce((s, p) => s + p.x, 0) / allPoints.length;
    const cy = allPoints.reduce((s, p) => s + p.y, 0) / allPoints.length;
    allPoints.sort(
      (a, b) => Math.atan2(a.y - cy, a.x - cx) - Math.atan2(b.y - cy, b.x - cx)
    );

    return allPoints;
  }

  function intersectSegmentWithDiagonal(
    a: { x: number; y: number },
    b: { x: number; y: number },
    threshold: number
  ): { x: number; y: number } | null {
    const sumA = a.x + a.y;
    const sumB = b.x + b.y;
    const denom = sumB - sumA;
    if (Math.abs(denom) < 1e-10) return null;

    const t = (threshold - sumA) / denom;
    if (t < 0 || t > 1) return null;

    return {
      x: a.x + t * (b.x - a.x),
      y: a.y + t * (b.y - a.y),
    };
  }

  const legendScale = scaleOrdinal<string, string>()
    .domain([
      "Zone E (81-100)",
      "Zone D (61-80)",
      "Zone C (41-60)",
      "Zone B (21-40)",
      "Zone A (0-20)",
    ])
    .range([
      "var(--gri-zone-e)",
      "var(--gri-zone-d)",
      "var(--gri-zone-c)",
      "var(--gri-zone-b)",
      "var(--gri-zone-a)",
    ]);
</script>

<div class="@container">
  <div class="flex flex-col items-center gap-4 @md:flex-row @md:items-start @md:gap-6">
    <!-- GRI Score Display -->
    <div class="flex shrink-0 flex-col items-center gap-1">
      <span class="text-xs font-medium uppercase tracking-wider text-muted-foreground">
        GRI
      </span>
      <div class="flex h-24 w-24 items-center justify-center rounded-full border-4 border-muted">
        <span class="text-3xl font-bold">{Math.round(gri.score ?? 0)}</span>
      </div>
      <p class="max-w-[120px] text-center text-[10px] leading-tight text-muted-foreground">
        Risk is indicated in percentiles &mdash; 0 is lowest risk and 100 is
        highest risk
      </p>
    </div>

    <!-- Scatter Plot -->
    <div class="w-full @md:max-w-[250px] @md:flex-1">
      <div class="aspect-square w-full">
        <Chart
          xScale={scaleLinear()}
          yScale={scaleLinear()}
          xDomain={[0, HYPO_MAX]}
          yDomain={[0, HYPER_MAX]}
          yReverse
          padding={{ top: 10, right: 10, bottom: 36, left: 46 }}
          tooltip={{ mode: "manual" }}
        >
          {#snippet children({ context })}
            <Svg>
              <!-- Zone polygons (rendered back-to-front: E first so A is on top) -->
              {#each zones as zone}
                <Polygon
                  points={zone.vertices.map((v) => ({
                    x: context.xScale(v.x),
                    y: context.yScale(v.y),
                  }))}
                  fill={zone.color}
                  fillOpacity={0.35}
                  stroke={zone.color}
                  strokeWidth={0.5}
                />
              {/each}

              {#if isTimeSeries}
                <!-- Time-series connecting lines (dotted) -->
                {#each timeSeriesPoints as point, i}
                  {#if i > 0}
                    {@const prev = timeSeriesPoints[i - 1]}
                    <line
                      x1={context.xScale(prev.hypo)}
                      y1={context.yScale(prev.hyper)}
                      x2={context.xScale(point.hypo)}
                      y2={context.yScale(point.hyper)}
                      stroke="hsl(var(--muted-foreground))"
                      stroke-width="1"
                      stroke-dasharray="2,2"
                    />
                  {/if}
                {/each}

                <!-- Time-series data points -->
                {#each timeSeriesPoints as point}
                  <circle
                    cx={context.xScale(point.hypo)}
                    cy={context.yScale(point.hyper)}
                    r={6}
                    fill={point.fill}
                    stroke={point.stroke}
                    stroke-width={2}
                    role="img"
                    aria-label="{formatPeriodLabel(point.periodStart)}: GRI {Math.round(point.gri?.score ?? 0)}"
                    onpointermove={(e) => context.tooltip?.show(e, point)}
                    onpointerleave={() => context.tooltip?.hide()}
                  />
                {/each}
              {:else}
                <!-- Patient position dot (single point mode) -->
                <Points
                  data={[
                    {
                      hypo: gri.hypoglycemiaComponent ?? 0,
                      hyper: gri.hyperglycemiaComponent ?? 0,
                    },
                  ]}
                  x="hypo"
                  y="hyper"
                  r={6}
                  class="fill-foreground stroke-background"
                  stroke-width="2"
                />
              {/if}

              <Axis
                placement="bottom"
                ticks={5}
                label="Hypoglycemia Component (%)"
                tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
              />
              <Axis
                placement="left"
                ticks={5}
                label="Hyperglycemia Component (%)"
                tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
              />
            </Svg>

            {#if isTimeSeries}
              <Tooltip.Root
                class="bg-popover text-popover-foreground rounded-md border p-3 shadow-lg"
              >
                {#snippet children({ data })}
                  {@const d = data as typeof timeSeriesPoints[0]}
                  <div class="min-w-44 space-y-1.5 text-xs">
                    <div class="font-semibold">
                      {formatPeriodLabel(d.periodStart)}
                    </div>

                    <div class="flex justify-between gap-4">
                      <span class="text-muted-foreground">GRI Score</span>
                      <span class="font-medium tabular-nums">
                        {Math.round(d.gri?.score ?? 0)}
                      </span>
                    </div>

                    {#if d.averageGlucoseMgdl != null}
                      <div class="flex justify-between gap-4">
                        <span class="text-muted-foreground">Avg Glucose</span>
                        <span class="font-medium tabular-nums">
                          {formatGlucoseValue(d.averageGlucoseMgdl, glucoseUnits.current)} {getUnitLabel(glucoseUnits.current)}
                        </span>
                      </div>
                    {/if}

                    {#if d.totalDailyDose != null}
                      <div class="flex justify-between gap-4">
                        <span class="text-muted-foreground">Avg TDD</span>
                        <span class="font-medium tabular-nums">
                          {d.totalDailyDose.toFixed(1)} U
                        </span>
                      </div>
                    {/if}

                    {#if d.averageDailyCarbs != null}
                      <div class="flex justify-between gap-4">
                        <span class="text-muted-foreground">Avg Carbs</span>
                        <span class="font-medium tabular-nums">
                          {Math.round(d.averageDailyCarbs)}g
                        </span>
                      </div>
                    {/if}
                  </div>
                {/snippet}
              </Tooltip.Root>
            {/if}
          {/snippet}
        </Chart>
      </div>
    </div>

    <!-- Legend -->
    <div class="shrink-0">
      <Legend
        scale={legendScale}
        variant="swatches"
        orientation="vertical"
        classes={{
          label: "text-[10px] text-muted-foreground",
          swatch: "rounded-sm",
        }}
      />
    </div>
  </div>
</div>
