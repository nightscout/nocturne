/* eslint-disable @typescript-eslint/consistent-type-assertions */
<script lang="ts">
  /**
   * Stacked sleep-stage composition chart for the trends page: one bar per
   * calendar day across the full selected range (gaps for days with no
   * recorded night), plus a compact stage-composition reference panel.
   */
  import { Chart, Svg, Axis, Tooltip } from "layerchart";
  import { scaleBand, scaleLinear, type ScaleBand } from "d3-scale";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { SLEEP_COMPOSITION_SEGMENTS } from "$lib/utils/sleep-stages";
  import { dayKeyFor, buildNightsByDayKey } from "$lib/utils/sleep-night-mapping";
  import { formatMinutesDuration } from "$lib/utils/sleep-format";
  import type { SleepNightSummary, SleepStageReferenceRangeSet } from "$lib/api";

  interface Props {
    /** Nights already filtered to the selected date range / source. */
    nights: SleepNightSummary[];
    /** Every calendar day in the selected (unpadded) range — including gaps. */
    days: Date[];
    meanDeepPct?: number | undefined;
    meanRemPct?: number | undefined;
    referenceRanges?: SleepStageReferenceRangeSet | undefined;
    /** deepMinutesDelta from summary.last7dVsPrior7d, when available. */
    deepMinutesDelta?: number | undefined;
  }

  let { nights, days, meanDeepPct, meanRemPct, referenceRanges, deepMinutesDelta }: Props =
    $props();

  type SegmentKey = (typeof SLEEP_COMPOSITION_SEGMENTS)[number]["key"];

  interface DaySegment {
    key: SegmentKey;
    label: string;
    lane: string;
    minutes: number;
    y0: number;
    y1: number;
  }

  interface DayRow {
    dayKey: string;
    date: Date;
    night: SleepNightSummary | undefined;
    segments: DaySegment[];
    totalMinutes: number;
  }

  const nightsByDayKey = $derived(buildNightsByDayKey(nights));

  const dayRows = $derived.by<DayRow[]>(() => {
    return days.map((date) => {
      const night = nightsByDayKey.get(dayKeyFor(date));
      let cumulative = 0;
      const segments: DaySegment[] = SLEEP_COMPOSITION_SEGMENTS.map((seg) => {
        const minutes = (night?.[seg.key] as number | undefined) ?? 0;
        const y0 = cumulative;
        cumulative += minutes;
        return { key: seg.key, label: seg.label, lane: seg.lane, minutes, y0, y1: cumulative };
      });
      return {
        dayKey: dayKeyFor(date).toString(),
        date,
        night,
        segments,
        totalMinutes: cumulative,
      };
    });
  });

  const hasUnspecified = $derived(nights.some((n) => (n.unspecifiedMinutes ?? 0) > 0));
  const visibleSegments = $derived(
    SLEEP_COMPOSITION_SEGMENTS.filter((seg) => seg.key !== "unspecifiedMinutes" || hasUnspecified)
  );

  const yMaxMinutes = $derived.by(() => {
    const max = Math.max(0, ...dayRows.map((r) => r.totalMinutes));
    if (max === 0) return 8 * 60;
    return Math.ceil((max + 30) / 60) * 60;
  });

  const xTickKeys = $derived.by(() => {
    const n = dayRows.length;
    if (n === 0) return [];
    const step = Math.max(1, Math.ceil(n / 8));
    return dayRows.filter((_, i) => i % step === 0).map((r) => r.dayKey);
  });

  function formatDayTick(key: string): string {
    const row = dayRows.find((r) => r.dayKey === key);
    if (!row) return "";
    return row.date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
  }

  /** Navigate to a night's drill-down using the backend's authoritative display date. */
  function navigateToNight(row: DayRow | undefined) {
    if (!row?.night?.displayDate) return;
    goto(resolve("/(authenticated)/reports/sleep/[date]", { date: row.night.displayDate }));
  }

  function handleKeydown(e: KeyboardEvent, row: DayRow) {
    if (!row.night) return;
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      navigateToNight(row);
    }
  }

  interface ReferenceRow {
    label: string;
    lane: string;
    meanPct: number | undefined;
    band?: { min: number; max: number };
  }

  const referenceRows = $derived.by<ReferenceRow[]>(() => [
    {
      label: "Deep",
      lane: SLEEP_COMPOSITION_SEGMENTS[0].lane,
      meanPct: meanDeepPct,
      band:
        referenceRanges?.deepMin != null && referenceRanges?.deepMax != null
          ? { min: referenceRanges.deepMin, max: referenceRanges.deepMax }
          : undefined,
    },
    {
      label: "REM",
      lane: SLEEP_COMPOSITION_SEGMENTS[1].lane,
      meanPct: meanRemPct,
      band:
        referenceRanges?.remMin != null && referenceRanges?.remMax != null
          ? { min: referenceRanges.remMin, max: referenceRanges.remMax }
          : undefined,
    },
  ]);
</script>

<div class="@container grid gap-6 @2xl:grid-cols-[2fr_1fr]">
  <div>
    <div class="sleep-composition-chart h-72 w-full">
      {#if dayRows.length > 0}
        <Chart
          data={dayRows}
          x="dayKey"
          xScale={scaleBand().paddingInner(0.25).paddingOuter(0.1)}
          y={(d: DayRow) => d.totalMinutes}
          yScale={scaleLinear()}
          yDomain={[0, yMaxMinutes]}
          padding={{ top: 10, right: 8, bottom: 30, left: 40 }}
          tooltipContext={{ mode: "manual" }}
        >
          {#snippet children({ context })}
            {@const xBandScale = context.xScale as unknown as ScaleBand<string>}
            <Svg>
              <Axis
                placement="left"
                rule
                grid
                ticks={4}
                format={(v: number) => `${Math.round(v / 60)}h`}
              />
              <Axis placement="bottom" rule ticks={xTickKeys} format={formatDayTick} />
              <!-- Stacked stage segments. Native SVG: layerchart marks each call
                   registerMark() on mount and every registration re-runs the
                   chart's mark deriveds over all marks, so days x segments rects
                   cost O(N^2). Coordinates are already pixel-space, so native
                   <rect> renders identically and registers nothing. -->
              {#each dayRows as row (row.dayKey)}
                {@const xPos = xBandScale(row.dayKey) ?? 0}
                {@const bandwidth = xBandScale.bandwidth()}
                {#each row.segments as segment (segment.key)}
                  {#if segment.minutes > 0}
                    <rect
                      x={xPos}
                      y={context.yScale(segment.y1)}
                      width={bandwidth}
                      height={Math.max(context.yScale(segment.y0) - context.yScale(segment.y1), 0)}
                      data-lane={segment.lane}
                      class="fill-[var(--lane-color)]"
                      rx={2}
                    />
                  {/if}
                {/each}
              {/each}

              <!-- Interaction overlay per day, on top of the bars: manual hover
                   tooltip + click / keyboard drill-through to the night. Native
                   <rect> keeps per-day focusability (a11y) and event handlers
                   without registering a mark. -->
              {#each dayRows as row (row.dayKey)}
                {@const xPos = xBandScale(row.dayKey) ?? 0}
                {@const bandwidth = xBandScale.bandwidth()}
                <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
                <rect
                  x={xPos}
                  y={0}
                  width={bandwidth}
                  height={context.height}
                  fill="transparent"
                  role={row.night ? "link" : undefined}
                  tabindex={row.night ? 0 : undefined}
                  aria-label={row.night
                    ? `Sleep session on ${row.date.toLocaleDateString(undefined, { month: "short", day: "numeric" })}`
                    : undefined}
                  class={row.night ? "cursor-pointer" : undefined}
                  onpointermove={(e: PointerEvent) => context.tooltip?.show(e, row)}
                  onpointerleave={() => context.tooltip?.hide()}
                  onclick={() => navigateToNight(row)}
                  onkeydown={(e: KeyboardEvent) => handleKeydown(e, row)}
                />
              {/each}
            </Svg>

            <Tooltip.Root>
              {#snippet children({ data })}
                {@const row = data as DayRow}
                <Tooltip.Header
                  value={`${row.night?.weekday ?? row.date.toLocaleDateString(undefined, { weekday: "short" })}, ${row.date.toLocaleDateString(undefined, { month: "short", day: "numeric" })}`}
                />
                {#if row.night}
                  {@const night = row.night}
                  <Tooltip.List>
                    {#each row.segments as segment (segment.key)}
                      {#if segment.key !== "unspecifiedMinutes" || segment.minutes > 0}
                        <Tooltip.Item
                          label={segment.label}
                          value={formatMinutesDuration(segment.minutes)}
                          color="var(--lane-color)"
                          props={{ color: { "data-lane": segment.lane } }}
                        />
                      {/if}
                    {/each}
                    <Tooltip.Item label="Awake" value={formatMinutesDuration(night.awakeMinutes ?? 0)} />
                    {#if night.sleepScore != null}
                      <Tooltip.Separator />
                      <Tooltip.Item
                        label="Sleep score"
                        value={`${Math.round(night.sleepScore)}${night.scoreSource === "Computed" ? " (Estimated)" : ""}`}
                      />
                    {/if}
                    {#if night.overnightTirPct != null}
                      <Tooltip.Item
                        label="Overnight TIR"
                        value={`${Math.round(night.overnightTirPct)}%`}
                      />
                    {/if}
                  </Tooltip.List>
                {:else}
                  <div class="text-xs text-muted-foreground">No recorded night</div>
                {/if}
              {/snippet}
            </Tooltip.Root>
          {/snippet}
        </Chart>
      {/if}
    </div>

    <!-- Legend -->
    <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
      {#each visibleSegments as seg (seg.key)}
        <span class="flex items-center gap-1.5">
          <span class="size-2 rounded-full bg-[var(--lane-color)]" data-lane={seg.lane}></span>
          {seg.label}
        </span>
      {/each}
    </div>
  </div>

  <!-- Stage composition reference panel -->
  <div class="space-y-4">
    {#each referenceRows as row (row.label)}
      <div class="space-y-1.5">
        <div class="flex items-center gap-2 text-sm">
          <span class="size-2.5 shrink-0 rounded-full bg-[var(--lane-color)]" data-lane={row.lane}></span>
          <span class="font-medium">{row.label}</span>
          <span class="ml-auto tabular-nums font-medium">
            {row.meanPct != null ? `${Math.round(row.meanPct)}%` : "—"}
          </span>
        </div>
        {#if row.band}
          <div class="relative h-1.5 w-full rounded-full bg-muted">
            <div
              class="absolute h-full rounded-full bg-muted-foreground/25"
              style:left="{Math.min(row.band.min, 100)}%"
              style:width="{Math.max(Math.min(row.band.max, 100) - Math.min(row.band.min, 100), 0)}%"
            ></div>
            {#if row.meanPct != null}
              <div
                class="absolute top-1/2 h-2.5 w-0.5 -translate-y-1/2 rounded-full bg-[var(--lane-color)]"
                data-lane={row.lane}
                style:left="{Math.min(Math.max(row.meanPct, 0), 100)}%"
              ></div>
            {/if}
          </div>
          <p class="text-xs text-muted-foreground">
            Typical range{referenceRanges?.label ? ` (${referenceRanges.label})` : ""}
            {Math.round(row.band.min)}–{Math.round(row.band.max)}%
          </p>
        {/if}
      </div>
    {/each}

    {#if deepMinutesDelta != null}
      <p class="text-xs text-muted-foreground">
        Deep sleep vs prior 7 nights: {deepMinutesDelta > 0 ? "+" : deepMinutesDelta < 0 ? "−" : "±"}{Math.abs(
          Math.round(deepMinutesDelta)
        )}m
      </p>
    {/if}
  </div>
</div>
