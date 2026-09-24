<script lang="ts">
  import { Chart, Svg, Axis, Polygon, Points, Tooltip } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import {
    GRIZone,
    type GlycemicRiskIndex,
    type GriTimelinePeriod,
  } from "$lib/api/generated/nocturne-api-client";
  import { formatGlucoseValue, formatMonthYear, getUnitLabel } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { patternClass, type TextureKey } from "$lib/components/charts/print/chart-print-patterns";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";

  interface Props {
    gri: GlycemicRiskIndex;
    timeSeriesData?: GriTimelinePeriod[];
  }

  let { gri, timeSeriesData }: Props = $props();

  const HYPO_MAX = 30;
  const HYPER_MAX = 60;
  const HYPO_WEIGHT = 3.0;
  const HYPER_WEIGHT = 1.6;

  /** Consensus GRI value of a component pair. */
  function griValue(hypo: number, hyper: number) {
    return HYPO_WEIGHT * hypo + HYPER_WEIGHT * hyper;
  }

  /** Pins a point to the plot's edge so nothing is drawn outside the axes. */
  function clampToPlot(hypo: number, hyper: number) {
    return {
      hypo: Math.min(Math.max(hypo, 0), HYPO_MAX),
      hyper: Math.min(Math.max(hyper, 0), HYPER_MAX),
      clamped: hypo > HYPO_MAX || hyper > HYPER_MAX,
    };
  }

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
      const stroke = gray > 200 ? "var(--border)" : fill;

      return {
        ...period,
        ...clampToPlot(period.gri?.hypoglycemiaComponent ?? 0, period.gri?.hyperglycemiaComponent ?? 0),
        index: i + 1,
        fill,
        stroke,
        ink: gray > 140 ? "black" : "white",
      };
    });
  });

  // Format period start date as "Month Year"
  function formatPeriodLabel(periodStart?: string): string {
    if (!periodStart) return "";
    const date = new Date(periodStart);
    return formatMonthYear(date);
  }

  // Zone boundaries: consensus grid lines where 3.0 x hypo + 1.6 x hyper = GRI threshold.
  // Zone E runs to the plot's far corner, since a GRI above 100 is capped into it.
  const zones: {
    label: string;
    range: string;
    texture: TextureKey;
    vertices: { x: number; y: number }[];
  }[] = [
    {
      label: "E",
      range: "81-100",
      texture: "gri-zone-e",
      vertices: buildZonePolygon(80, griValue(HYPO_MAX, HYPER_MAX)),
    },
    {
      label: "D",
      range: "61-80",
      texture: "gri-zone-d",
      vertices: buildZonePolygon(60, 80),
    },
    {
      label: "C",
      range: "41-60",
      texture: "gri-zone-c",
      vertices: buildZonePolygon(40, 60),
    },
    {
      label: "B",
      range: "21-40",
      texture: "gri-zone-b",
      vertices: buildZonePolygon(20, 40),
    },
    {
      label: "A",
      range: "0-20",
      texture: "gri-zone-a",
      vertices: buildZonePolygon(0, 20),
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
      const value = griValue(corner.x, corner.y);
      if (value >= lower && value <= upper) {
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
    const valueA = griValue(a.x, a.y);
    const valueB = griValue(b.x, b.y);
    const denom = valueB - valueA;
    if (Math.abs(denom) < 1e-10) return null;

    const t = (threshold - valueA) / denom;
    if (t < 0 || t > 1) return null;

    return {
      x: a.x + t * (b.x - a.x),
      y: a.y + t * (b.y - a.y),
    };
  }

  const zoneKey = $derived(
    zones.map((zone) => ({
      texture: zone.texture,
      label: `Zone ${zone.label} (${zone.range})`,
    }))
  );

  const ZONE_LETTER: Record<GRIZone, string> = {
    [GRIZone.A]: "A",
    [GRIZone.B]: "B",
    [GRIZone.C]: "C",
    [GRIZone.D]: "D",
    [GRIZone.E]: "E",
  };

  const currentZone = $derived.by(() => {
    if (gri.zone == null) return undefined;
    const letter = ZONE_LETTER[gri.zone];
    return zones.find((zone) => zone.label === letter);
  });

  const singlePoint = $derived(
    clampToPlot(gri.hypoglycemiaComponent ?? 0, gri.hyperglycemiaComponent ?? 0)
  );
  const anyClamped = $derived(
    isTimeSeries ? timeSeriesPoints.some((p) => p.clamped) : singlePoint.clamped
  );
</script>

<div class="@container">
  <div class="flex flex-col items-center gap-4 @md:flex-row @md:items-start @md:gap-6">
    <div class="w-full @md:max-w-[250px] @md:flex-1">
      <div class="aspect-square w-full">
        <Chart
          xScale={scaleLinear()}
          yScale={scaleLinear()}
          xDomain={[0, HYPO_MAX]}
          yDomain={[0, HYPER_MAX]}
          yReverse
          padding={{ top: 10, right: 10, bottom: 36, left: 46 }}
          tooltipContext={{ mode: "manual" }}
        >
          {#snippet children({ context })}
            <Svg>
              <!-- E first so A draws on top. -->
              {#each zones as zone (zone.label)}
                <Polygon
                  points={zone.vertices.map((v) => ({
                    x: context.xScale(v.x),
                    y: context.yScale(v.y),
                  }))}
                  fill="var(--{zone.texture})"
                  fillOpacity={0.35}
                  stroke="var(--{zone.texture})"
                  strokeWidth={0.5}
                  class={patternClass(zone.texture)}
                />
              {/each}

              {#if isTimeSeries}
                {#each timeSeriesPoints as point, i (i)}
                  {#if i > 0}
                    {@const prev = timeSeriesPoints[i - 1]}
                    <line
                      x1={context.xScale(prev.hypo)}
                      y1={context.yScale(prev.hyper)}
                      x2={context.xScale(point.hypo)}
                      y2={context.yScale(point.hyper)}
                      stroke="var(--muted-foreground)"
                      stroke-width="1"
                      stroke-dasharray="2,2"
                    />
                  {/if}
                {/each}

                {#each timeSeriesPoints as point, i (i)}
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
                  <!-- Paper has no tooltip, so each point carries its number in the printed period list. -->
                  <text
                    x={context.xScale(point.hypo)}
                    y={context.yScale(point.hyper)}
                    dy="0.35em"
                    text-anchor="middle"
                    font-size="8"
                    font-weight="600"
                    fill={point.ink}
                    pointer-events="none"
                    class="hidden print:inline"
                  >
                    {point.index}
                  </text>
                {/each}
              {:else}
                <Points
                  data={[singlePoint]}
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
                tickLabelProps={{ class: "text-2xs fill-muted-foreground" }}
              />
              <Axis
                placement="left"
                ticks={5}
                label="Hyperglycemia Component (%)"
                tickLabelProps={{ class: "text-2xs fill-muted-foreground" }}
              />
            </Svg>

            {#if isTimeSeries}
              <Tooltip.Root
                class="bg-popover text-popover-foreground rounded-md border p-3 shadow-lg"
              >
                {#snippet children({ data })}
                  {@const d: (typeof timeSeriesPoints)[number] = data}
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

    <div class="shrink-0 space-y-3">
      <div class="border-b border-border pb-3">
        <dl class="m-0">
          <dt class="text-xs text-muted-foreground">GRI</dt>
          <dd class="m-0 mt-0.5">
            <span class="text-2xl font-semibold tabular-nums">{Math.round(gri.score ?? 0)}</span>
            {#if currentZone}
              <span class="ml-1 text-sm text-muted-foreground">
                Zone {currentZone.label} ({currentZone.range})
              </span>
            {/if}
          </dd>
        </dl>
        <p class="mt-1 max-w-40 text-2xs leading-tight text-muted-foreground">
          Risk is indicated in percentiles &mdash; 0 is lowest risk and 100 is
          highest risk
        </p>
      </div>
      <ChartKey items={zoneKey} class="flex-col items-start gap-y-1.5 text-2xs" />
      {#if isTimeSeries}
        <ol class="hidden space-y-0.5 text-2xs text-muted-foreground print:block">
          {#each timeSeriesPoints as point (point.index)}
            <li class="tabular-nums">
              <span class="font-semibold text-foreground">{point.index}</span>
              {formatPeriodLabel(point.periodStart)} &middot; GRI {Math.round(point.gri?.score ?? 0)}
            </li>
          {/each}
        </ol>
      {/if}
      {#if anyClamped}
        <p class="max-w-40 text-2xs text-muted-foreground">
          Points beyond the axes are drawn at the edge.
        </p>
      {/if}
    </div>
  </div>
</div>
