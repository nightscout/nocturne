<script lang="ts">
  import { Chart, Svg, Axis, Tooltip } from "layerchart";
  import { scaleTime, scaleLinear } from "d3-scale";
  import type { ScaleTime } from "d3-scale";
  import { getActogramData } from "$api/actogram.remote";
  import { getBasalSeries } from "$api/generated/chartDatas.generated.remote";
  import { bg, bgLabel, toDate } from "$lib/utils/formatting";
  import { resolveChartColor } from "$lib/utils/chart-colors";
  import {
    laneForStage,
    HYPNOGRAM_LANE_ORDER,
    HYPNOGRAM_LANE_LABELS,
  } from "$lib/utils/sleep-stages";
  import { BasalDeliveryOrigin } from "$lib/api";
  import type { SleepStageInterval, SleepDawnPhenomenon } from "$lib/api";

  interface Props {
    stages: SleepStageInterval[] | undefined;
    startTime: Date;
    endTime: Date;
    dawnPhenomenon?: SleepDawnPhenomenon | null;
  }

  let { stages, startTime, endTime, dawnPhenomenon }: Props = $props();

  const LANE_HEIGHT = 36;
  const BASAL_LANE_HEIGHT = 28;
  const LABEL_WIDTH = 76;
  const WINDOW_PADDING_MS = 30 * 60 * 1000;
  // Nearest CGM reading counts as "at" the hovered time within this window.
  const GLUCOSE_STALENESS_MS = 15 * 60 * 1000;

  const timeFormatter = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" });

  // Chart window: session ±30min so the glucose trace shows lead-in/lead-out context.
  const windowStart = $derived(new Date(startTime.getTime() - WINDOW_PADDING_MS));
  const windowEnd = $derived(new Date(endTime.getTime() + WINDOW_PADDING_MS));

  // Overlay data. Queried directly (not via contextResource) so a failure in
  // either fetch degrades to a stages-only hypnogram instead of tripping the
  // layout-level ResourceGuard for the whole page.
  const actogramQuery = $derived(
    getActogramData({ from: windowStart.getTime(), to: windowEnd.getTime() })
  );
  const basalQuery = $derived(
    getBasalSeries({ startTime: windowStart.getTime(), endTime: windowEnd.getTime() })
  );

  const glucosePoints = $derived(
    actogramQuery.error ? [] : (actogramQuery.current?.glucoseData ?? [])
  );
  const glucoseYMax = $derived(actogramQuery.current?.thresholds?.glucoseYMax ?? 300);
  const hasGlucose = $derived(glucosePoints.length > 0);

  const basalPoints = $derived.by(() => {
    if (basalQuery.error) return [];
    return (basalQuery.current ?? [])
      .filter((p) => p.timestamp != null)
      .toSorted((a, b) => (a.timestamp ?? 0) - (b.timestamp ?? 0));
  });
  const hasBasal = $derived(basalPoints.length > 0);
  const maxBasalRate = $derived.by(() => {
    if (!hasBasal) return 1;
    const max = Math.max(...basalPoints.map((p) => Math.max(p.rate ?? 0, p.scheduledRate ?? 0)));
    return max > 0 ? max : 1;
  });

  interface StageSpan {
    start: Date;
    end: Date;
    lane: string;
    label: string;
  }

  const normalizedStages = $derived.by((): StageSpan[] => {
    const spans: StageSpan[] = [];
    for (const s of stages ?? []) {
      const start = toDate(s.startTime);
      const end = toDate(s.endTime);
      if (!start || !end) continue;
      const stage = s.stage ?? "Unknown";
      spans.push({
        start,
        end,
        lane: laneForStage(stage),
        label: stage,
      });
    }
    return spans;
  });

  const hasStages = $derived(normalizedStages.length > 0);
  const hasUnspecified = $derived(normalizedStages.some((s) => s.lane === "unspecified"));

  // Classic hypnogram order top-to-bottom; Unspecified only added when it occurs.
  const lanes = $derived<string[]>(
    hasStages ? [...HYPNOGRAM_LANE_ORDER, ...(hasUnspecified ? (["unspecified"] as const) : [])] : ["unspecified"]
  );

  const laneIndex = $derived(new Map(lanes.map((lane, i) => [lane, i] as const)));

  // Fallback: a single full-width bar in the Unspecified lane when there's no stage data at all.
  const displaySpans = $derived<StageSpan[]>(
    hasStages
      ? normalizedStages
      : [
          {
            start: startTime,
            end: endTime,
            lane: "unspecified",
            label: "Unspecified",
          },
        ]
  );

  // Vertical layout: stage lanes, then the basal lane (when pump data exists),
  // then the time axis.
  const stagesHeight = $derived(lanes.length * LANE_HEIGHT);
  const basalTop = $derived(stagesHeight + 2);
  const chartHeight = $derived(stagesHeight + (hasBasal ? BASAL_LANE_HEIGHT + 4 : 0));

  // Basal lane mapping + rendering, mirroring the glucose chart's BasalTrack:
  // a scheduled-rate step line under the delivered rate, colored by origin.
  const basalFloor = $derived(basalTop + BASAL_LANE_HEIGHT - 2);
  function basalLaneY(rate: number): number {
    return basalFloor - (rate / maxBasalRate) * (BASAL_LANE_HEIGHT - 4);
  }

  // Matches BasalTrack.getBasalOpacity so temp/manual delivery reads stronger
  // than scheduled and suspended reads faint.
  function basalOpacity(origin: BasalDeliveryOrigin | undefined): number {
    switch (origin) {
      case BasalDeliveryOrigin.Algorithm: return 0.8;
      case BasalDeliveryOrigin.Manual: return 0.9;
      case BasalDeliveryOrigin.Suspended: return 0.5;
      case BasalDeliveryOrigin.Inferred: return 0.4;
      default: return 0.6;
    }
  }

  /** Step-after path over scheduledRate, held until the next point (last held to windowEnd). */
  function buildScheduledPath(xScale: (d: Date) => number): string {
    let d = "";
    for (let i = 0; i < basalPoints.length; i++) {
      const p = basalPoints[i];
      const x1 = xScale(new Date(p.timestamp ?? 0));
      const x2 =
        i < basalPoints.length - 1 ? xScale(new Date(basalPoints[i + 1].timestamp ?? 0)) : xScale(windowEnd);
      const y = basalLaneY(p.scheduledRate ?? 0);
      d += `${i === 0 ? "M" : "L"} ${x1} ${y} L ${x2} ${y} `;
    }
    return d.trim();
  }

  const hasScheduledBasal = $derived(basalPoints.some((p) => (p.scheduledRate ?? 0) > 0));

  // Glucose overlay spans the stage-lane region.
  const glucoseScale = $derived(scaleLinear([0, glucoseYMax], [stagesHeight, 0]));
  const glucoseTicks = $derived(hasGlucose ? glucoseScale.ticks(3).filter((v) => v > 0) : []);

  function buildGlucosePath(xScale: (d: Date) => number): string {
    return glucosePoints
      .map((p, i) => `${i === 0 ? "M" : "L"} ${xScale(new Date(p.mills))} ${glucoseScale(p.sgv)}`)
      .join(" ");
  }

  const dawnWindow = $derived.by(() => {
    if (!dawnPhenomenon) return null;
    const start = toDate(dawnPhenomenon.windowStart);
    const end = toDate(dawnPhenomenon.windowEnd);
    if (!start || !end) return null;
    return { start, end };
  });

  // ---- Tooltip lookups (presentational: nearest reading / containing span) ----

  interface TooltipData {
    time: Date;
    glucose: { sgv: number; color: string } | null;
    stageLane: string | null;
    basalRate: number | null;
    scheduledBasalRate: number | null;
  }

  function nearestGlucose(at: number): TooltipData["glucose"] {
    let best: (typeof glucosePoints)[number] | null = null;
    let bestDist = Infinity;
    for (const p of glucosePoints) {
      const dist = Math.abs(p.mills - at);
      if (dist < bestDist) {
        best = p;
        bestDist = dist;
      }
    }
    if (!best || bestDist > GLUCOSE_STALENESS_MS) return null;
    return { sgv: best.sgv, color: best.color };
  }

  function stageAt(at: number): string | null {
    const span = displaySpans.find((s) => s.start.getTime() <= at && at < s.end.getTime());
    return span ? span.lane : null;
  }

  function basalAt(at: number): { rate: number | null; scheduled: number | null } {
    if (!hasBasal) return { rate: null, scheduled: null };
    let rate: number | null = null;
    let scheduled: number | null = null;
    for (const p of basalPoints) {
      if ((p.timestamp ?? 0) > at) break;
      rate = p.rate ?? 0;
      scheduled = p.scheduledRate ?? null;
    }
    return { rate, scheduled };
  }

  function tooltipDataAt(time: Date): TooltipData {
    const at = time.getTime();
    const basal = basalAt(at);
    return {
      time,
      glucose: nearestGlucose(at),
      stageLane: stageAt(at),
      basalRate: basal.rate,
      scheduledBasalRate: basal.scheduled,
    };
  }
</script>

<div class="w-full overflow-x-auto print:overflow-visible">
  <div style="min-width: 480px; height: {chartHeight + 28}px;">
    <Chart
      data={[]}
      xScale={scaleTime()}
      yScale={scaleLinear()}
      xDomain={[windowStart, windowEnd]}
      yDomain={[0, chartHeight]}
      padding={{ top: 4, right: hasGlucose ? 48 : 8, bottom: 24, left: LABEL_WIDTH }}
    >
      {#snippet children({ context })}
        <Svg>
          <!-- Lane backgrounds + labels -->
          {#each lanes as lane, i (lane)}
            {@const y = i * LANE_HEIGHT}
            <rect
              x={context.xScale(windowStart)}
              y={y}
              width={Math.max(context.xScale(windowEnd) - context.xScale(windowStart), 0)}
              height={LANE_HEIGHT - 2}
              fill="var(--muted)"
              class="opacity-15"
            />
            <text
              x={-LABEL_WIDTH + 8}
              y={y + LANE_HEIGHT / 2 + 4}
              class="text-[10px] fill-muted-foreground font-medium"
            >
              {HYPNOGRAM_LANE_LABELS[lane]}
            </text>
          {/each}

          <!-- Dawn window overlay -->
          {#if dawnWindow}
            {@const dx = context.xScale(dawnWindow.start)}
            {@const dw = Math.max(context.xScale(dawnWindow.end) - dx, 0)}
            <rect x={dx} y={0} width={dw} height={stagesHeight} fill="var(--chart-4)" class="opacity-10 pointer-events-none" />
            <text x={dx + 4} y={10} class="text-[9px] fill-muted-foreground pointer-events-none">
              pre-wake window
            </text>
          {/if}

          <!-- Stage spans -->
          {#each displaySpans as span, i (i)}
            {@const y = (laneIndex.get(span.lane) ?? 0) * LANE_HEIGHT}
            {@const x1 = context.xScale(span.start)}
            {@const x2 = context.xScale(span.end)}
            <rect
              x={x1}
              y={y + 2}
              width={Math.max(x2 - x1, 1)}
              height={LANE_HEIGHT - 6}
              data-lane={span.label.toLowerCase()}
              rx={2}
              class="fill-[var(--lane-color)] opacity-70"
            />
          {/each}

          <!-- Glucose overlay across the stage-lane region -->
          {#if hasGlucose}
            <path
              d={buildGlucosePath((d) => context.xScale(d))}
              fill="none"
              class="stroke-muted-foreground/50 pointer-events-none"
              stroke-width="1.5"
            />
            {#each glucosePoints as point, i (i)}
              <circle
                cx={context.xScale(new Date(point.mills))}
                cy={glucoseScale(point.sgv)}
                r={2}
                fill={point.color}
                class="opacity-80 pointer-events-none"
              />
            {/each}

            <!-- Right-side glucose axis in display units -->
            {#each glucoseTicks as tick (tick)}
              <text
                x={context.width + 6}
                y={glucoseScale(tick) + 3}
                class="text-[9px] fill-muted-foreground tabular-nums"
              >
                {bg(tick)}
              </text>
            {/each}
            <text x={context.width + 6} y={-2} class="text-[9px] fill-muted-foreground">
              {bgLabel()}
            </text>
          {/if}

          <!-- Basal swim lane -->
          {#if hasBasal}
            <rect
              x={context.xScale(windowStart)}
              y={basalTop}
              width={Math.max(context.xScale(windowEnd) - context.xScale(windowStart), 0)}
              height={BASAL_LANE_HEIGHT}
              fill="var(--muted)"
              class="opacity-15"
            />
            <text
              x={-LABEL_WIDTH + 8}
              y={basalTop + BASAL_LANE_HEIGHT / 2 + 4}
              class="text-[10px] fill-muted-foreground font-medium"
            >
              Basal
            </text>
            <!-- Delivered rate, step area colored by origin -->
            {#each basalPoints as point, i (i)}
              {@const segStart = new Date(point.timestamp ?? 0)}
              {@const segEnd = i < basalPoints.length - 1 ? new Date(basalPoints[i + 1].timestamp ?? 0) : windowEnd}
              {@const h = basalFloor - basalLaneY(point.rate ?? 0)}
              {#if h > 0}
                {@const x1 = context.xScale(segStart)}
                {@const x2 = context.xScale(segEnd)}
                <rect
                  x={x1}
                  y={basalLaneY(point.rate ?? 0)}
                  width={Math.max(x2 - x1, 1)}
                  height={h}
                  fill={resolveChartColor(point.fillColor ?? "insulin-basal")}
                  style="opacity: {basalOpacity(point.origin)}"
                />
              {/if}
            {/each}

            <!-- Scheduled rate, dashed step line -->
            {#if hasScheduledBasal}
              <path
                d={buildScheduledPath((d) => context.xScale(d))}
                fill="none"
                class="stroke-muted-foreground/50 pointer-events-none"
                stroke-width="1"
                stroke-dasharray="3,3"
              />
            {/if}
          {/if}

          <!-- Hourly time axis -->
          <Axis
            placement="bottom"
            rule
            ticks={8}
            format={(d: Date) => timeFormatter.format(d)}
            tickLabelProps={{ class: "text-[10px] fill-muted-foreground" }}
          />

          <!-- Interaction overlay for the tooltip (topmost layer) -->
          <rect
            role="presentation"
            x={0}
            y={0}
            width={context.width}
            height={context.height}
            fill="transparent"
            onpointermove={(e) => {
              const svgRect = e.currentTarget.closest("svg")?.getBoundingClientRect();
              if (!svgRect) return;
              const localX = e.clientX - svgRect.left - LABEL_WIDTH;
              const time = (context.xScale as unknown as ScaleTime<number, number>).invert(localX);
              context.tooltip?.show(e, tooltipDataAt(time) satisfies TooltipData);
            }}
            onpointerleave={() => context.tooltip?.hide()}
          />
        </Svg>

        <Tooltip.Root
          class="print:hidden bg-popover/95 text-popover-foreground rounded-lg border border-border px-2.5 py-1.5 shadow-xl"
        >
          {#snippet children({ data: tooltipData })}
            {@const d = tooltipData as TooltipData}
            {#if d}
              <div class="space-y-1 text-xs">
                <div class="font-medium tabular-nums">{timeFormatter.format(d.time)}</div>
                {#if d.glucose}
                  <div class="flex items-center gap-1.5">
                    <div class="size-2 rounded-full" style:background={d.glucose.color}></div>
                    <span class="text-muted-foreground">Glucose</span>
                    <span class="ml-auto pl-3 font-mono font-medium tabular-nums">
                      {bg(d.glucose.sgv)} {bgLabel()}
                    </span>
                  </div>
                {/if}
                {#if d.stageLane}
                  <div class="flex items-center gap-1.5">
                    <div class="size-2 rounded-full bg-[var(--lane-color)]" data-lane={d.stageLane}></div>
                    <span class="text-muted-foreground">Stage</span>
                    <span class="ml-auto pl-3 font-medium">{HYPNOGRAM_LANE_LABELS[d.stageLane]}</span>
                  </div>
                {/if}
                {#if d.basalRate != null}
                  <div class="flex items-center gap-1.5">
                    <div class="size-2 rounded-full" style:background="var(--insulin-basal)"></div>
                    <span class="text-muted-foreground">Basal</span>
                    <span class="ml-auto pl-3 font-mono font-medium tabular-nums">
                      {d.basalRate.toFixed(2)} U/h
                    </span>
                  </div>
                {/if}
                {#if d.scheduledBasalRate != null && d.scheduledBasalRate !== d.basalRate}
                  <div class="flex items-center gap-1.5">
                    <div class="size-2 rounded-full border border-muted-foreground/50"></div>
                    <span class="text-muted-foreground">Scheduled</span>
                    <span class="ml-auto pl-3 font-mono font-medium tabular-nums">
                      {d.scheduledBasalRate.toFixed(2)} U/h
                    </span>
                  </div>
                {/if}
              </div>
            {/if}
          {/snippet}
        </Tooltip.Root>
      {/snippet}
    </Chart>
  </div>

  {#if !hasStages}
    <p class="mt-2 text-sm text-muted-foreground">No stage data recorded for this session.</p>
  {/if}
</div>
