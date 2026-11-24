<script lang="ts">
  import { Area, ChartClipPath, Highlight, getChartContext } from "layerchart";
  import { bisector, curveMonotoneX } from "d3";
  import {
    bubbleScale,
    DEFAULT_BUBBLE_CARB_RATIO,
    treatmentBubble,
  } from "../view/nightscout-sizing.js";
  import BolusMarker from "../markers/BolusMarker.svelte";
  import CarbMarker from "../markers/CarbMarker.svelte";
  import { carbMarkerKey, markerKey } from "../keys.js";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import type { GlucosePoint, SeriesPoint } from "../view/chart-data-view.svelte.js";
  import {
    placeCenteredLabels,
    placeTrailingLabels,
    PRINT_LABEL_CHAR_WIDTH,
    type LabelCandidate,
  } from "../view/marker-label-layout.js";
  import { MARKER_HALF_WIDTH } from "../icons/marker-shapes.js";
  import { patternClass } from "../print/patterns.js";
  import TrackAxis from "./TrackAxis.svelte";
  import TrackLabel from "./TrackLabel.svelte";

  interface Props {
    carbRatio?: number;
    onMarkerClick?: (treatmentId: string) => void;
    onPointClick?: (time: Date) => void;
    /**
     * "nightscout" draws each entry as cgm-remote-monitor does: a carb/insulin pie on the
     * reading nearest its time, sized by its amount, with no IOB/COB track.
     */
    variant?: "default" | "nightscout";
  }

  let { carbRatio, onMarkerClick, onPointClick, variant = "default" }: Props = $props();
  const ratio = $derived(
    carbRatio ?? (variant === "nightscout" ? DEFAULT_BUBBLE_CARB_RATIO : 15)
  );

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const iobData = $derived(ctx.view.iobData);
  const cobData = $derived(ctx.view.cobData);
  const bolusMarkers = $derived(ctx.view.bolusMarkers);
  const carbMarkers = $derived(ctx.view.carbMarkers);
  const iobCobLayout = $derived(ctx.layout.iobCob);

  const showIob = $derived(ctx.legend?.iob ?? true);
  const showCob = $derived(ctx.legend?.cob ?? true);
  const showBolus = $derived(ctx.legend?.bolus ?? true);
  const showCarbs = $derived(ctx.legend?.carbs ?? true);

  const effectiveOnPointClick = $derived(
    onPointClick ?? ((time: Date) => ctx.inspection?.inspectFromTrack(time))
  );
  const effectiveOnMarkerClick = $derived(
    onMarkerClick ?? ((_treatmentId: string) => {})
  );

  // ---- Label declutter ----
  // Which markers get their text at this zoom. Only the markers inside the
  // plot compete: the rest are clipped, labels and all. Recomputed on every
  // pan or zoom because it reads the x scale.
  type BolusMarkerItem = (typeof bolusMarkers)[number];
  type CarbMarkerItem = (typeof carbMarkers)[number];

  const onScreen = (x: number) => x >= 0 && x <= chartCtx.width;

  /** The track's own "IOB/COB" name, drawn at x=4 in the label rows; on paper it is in the gutter. */
  const TRACK_NAME_SPAN = { left: 0, right: 44 };
  const labelObstacles = $derived(ctx.printing ? [] : [TRACK_NAME_SPAN]);
  const amountCharWidth = $derived(ctx.printing ? PRINT_LABEL_CHAR_WIDTH : undefined);
  /** Space between a glyph's edge and its meal name (see CarbMarker). */
  const MEAL_LABEL_GAP = 3;

  const bolusLabelCandidates = $derived.by(() => {
    const out: LabelCandidate<BolusMarkerItem>[] = [];
    for (const m of bolusMarkers) {
      const x = chartCtx.xScale(m.time);
      if (!onScreen(x)) continue;
      const insulin = m.insulin ?? 0;
      out.push({ item: m, x, text: ctx.labels.insulinAmount(insulin.toFixed(1)), priority: insulin });
    }
    return out;
  });

  const carbLabelCandidates = $derived.by(() => {
    const out: LabelCandidate<CarbMarkerItem>[] = [];
    for (const m of carbMarkers) {
      const x = chartCtx.xScale(m.time);
      if (!onScreen(x)) continue;
      const carbs = m.carbs ?? 0;
      out.push({ item: m, x, text: ctx.labels.carbAmount(String(carbs)), priority: carbs });
    }
    return out;
  });

  // The two amount rows are stacked, so a meal's carb and bolus labels never
  // contest one another and each row is placed on its own.
  const bolusLabelVisible = $derived(
    showBolus
      ? placeCenteredLabels(bolusLabelCandidates, labelObstacles, amountCharWidth)
      : new Set<BolusMarkerItem>()
  );
  const carbLabelVisible = $derived(
    showCarbs
      ? placeCenteredLabels(carbLabelCandidates, labelObstacles, amountCharWidth)
      : new Set<CarbMarkerItem>()
  );

  // A meal name hangs beside the waist, where every visible glyph is drawn.
  const mealLabelVisible = $derived.by(() => {
    if (!showCarbs) return new Set<CarbMarkerItem>();
    const glyphXs = [
      ...(showBolus ? bolusLabelCandidates.map((c) => c.x) : []),
      ...carbLabelCandidates.map((c) => c.x),
    ];
    const named = carbLabelCandidates
      .map((c) => ({ ...c, text: carbMarkerName(c.item) ?? "" }))
      .filter((c) => c.text);
    return placeTrailingLabels(named, glyphXs, MARKER_HALF_WIDTH, MEAL_LABEL_GAP, amountCharWidth);
  });

  // ---- Nightscout bubbles ----
  // One entry can carry both a bolus and carbs; the engine hands them over as two markers,
  // and both halves of the pie take the entry's size.
  const entryKey = (id: string | undefined, time: Date) => id || `t${time.getTime()}`;
  const entryAmounts = $derived.by(() => {
    const amounts: Record<string, { carbs: number; insulin: number }> = {};
    const add = (key: string, carbs: number, insulin: number) => {
      const entry = (amounts[key] ??= { carbs: 0, insulin: 0 });
      entry.carbs += carbs;
      entry.insulin += insulin;
    };
    if (showBolus) for (const m of bolusMarkers) add(entryKey(m.treatmentId, m.time), 0, m.insulin ?? 0);
    if (showCarbs) for (const m of carbMarkers) add(entryKey(m.treatmentId, m.time), m.carbs ?? 0, 0);
    return amounts;
  });
  const entryCount = $derived(Object.keys(entryAmounts).length);
  const bubbleDivisor = $derived.by(() => {
    const [from, to] = chartCtx.xScale.domain();
    return bubbleScale(chartCtx.width, Number(new Date(to)) - Number(new Date(from)));
  });
  function bubbleFor(key: string) {
    const entry = entryAmounts[key] ?? { carbs: 0, insulin: 0 };
    return {
      ...entry,
      bubble: treatmentBubble(entry.carbs, entry.insulin, ratio, bubbleDivisor, entryCount),
    };
  }

  const glucoseData = $derived(ctx.view.glucoseData);
  const nearestReading = bisector((d: GlucosePoint) => d.time).center;
  /** Nightscout pins an entry to the reading nearest its time. */
  function glucoseY(time: Date): number | null {
    if (glucoseData.length === 0) return null;
    const sgv = glucoseData[nearestReading(glucoseData, time)]?.sgv;
    return sgv == null ? null : chartCtx.yScale(ctx.layout.glucose.scale(sgv));
  }

  /** Food names win over the meal period, which only stands in when none were logged. */
  function carbMarkerName(marker: CarbMarkerItem): string | undefined {
    if (marker.label) return marker.label;
    return marker.mealPeriod ? ctx.labels.mealPeriod(marker.mealPeriod) : undefined;
  }

  // Bisector for finding nearest data point
  function findSeriesValue(
    series: SeriesPoint[],
    time: Date
  ): SeriesPoint | undefined {
    if (series.length === 0) return undefined;
    let lo = 0;
    let hi = series.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (series[mid].time < time) lo = mid + 1;
      else hi = mid;
    }
    const d0 = series[lo - 1];
    const d1 = series[lo];
    if (!d0) return d1;
    if (!d1) return d0;
    return time.getTime() - d0.time.getTime() >
      d1.time.getTime() - time.getTime()
      ? d1
      : d0;
  }
</script>

{#if variant === "nightscout"}
  <ChartClipPath>
    {#if showBolus}
      {#each bolusMarkers as marker, i (markerKey(marker.treatmentId, marker.time, i))}
        {@const yPos = glucoseY(marker.time)}
        {@const entry = bubbleFor(entryKey(marker.treatmentId, marker.time))}
        {#if yPos != null && (marker.insulin ?? 0) > 0}
          <BolusMarker
            xPos={chartCtx.xScale(marker.time)}
            {yPos}
            insulin={marker.insulin ?? 0}
            isOverride={marker.isOverride ?? false}
            bolusType={marker.bolusType}
            treatmentId={marker.treatmentId ?? ""}
            onMarkerClick={effectiveOnMarkerClick}
            shape="bubble"
            bubble={entry.bubble}
            outlineCarbHalf={entry.carbs <= 0}
          />
        {/if}
      {/each}
    {/if}
    {#if showCarbs}
      {#each carbMarkers as marker, i (markerKey(carbMarkerKey(marker), marker.time, i))}
        {@const yPos = glucoseY(marker.time)}
        {@const entry = bubbleFor(entryKey(marker.treatmentId, marker.time))}
        {#if yPos != null && (marker.carbs ?? 0) > 0}
          <CarbMarker
            xPos={chartCtx.xScale(marker.time)}
            {yPos}
            carbs={marker.carbs ?? 0}
            label={null}
            treatmentId={marker.treatmentId ?? ""}
            onMarkerClick={effectiveOnMarkerClick}
            shape="bubble"
            bubble={entry.bubble}
            outlineInsulinHalf={entry.insulin <= 0}
          />
        {/if}
      {/each}
    {/if}
  </ChartClipPath>
{:else if iobCobLayout}
  {@const iobScale = iobCobLayout.scale}
  {@const iobZero = iobCobLayout.zero}
  {@const iobAxisScale = iobCobLayout.axisScale}
  <!-- Treatment markers share one baseline so a carb entry (rising above it) and
       a bolus (hanging below it) at the same time compose into one diamond.
       Magnitude is conveyed by the marker labels, not height. -->
  {@const markerBaselineY = (iobCobLayout.top + iobCobLayout.bottom) / 2}

  <TrackAxis scale={iobAxisScale} unit={ctx.labels.insulinUnits} />
  <TrackLabel label={ctx.labels.iobCob} top={iobCobLayout.top} bottom={iobCobLayout.bottom} />

  <ChartClipPath>
    <!-- COB area (scaled by carb ratio to show on IOB-equivalent scale) -->
    {#if cobData.length > 0 && cobData.some((d) => d.value > 0.01) && showCob}
      <Area
        data={cobData}
        x={(d) => d.time}
        y0={() => iobZero}
        y1={(d) => iobScale(d.value / ratio)}
        motion="spring"
        curve={curveMonotoneX}
        fill=""
        class="fill-carbs/40 {patternClass('carbs')}"
      />
    {/if}

    <!-- IOB area, drawn from zero: up for insulin on board, down for basal withheld -->
    {#if iobData.length > 0 && iobData.some((d) => Math.abs(d.value) > 0.01) && showIob}
      <Area
        data={iobData}
        x={(d) => d.time}
        y0={() => iobZero}
        y1={(d) => iobScale(d.value)}
        motion="spring"
        curve={curveMonotoneX}
        fill=""
        class="fill-iob-basal/60"
      />
    {/if}
  </ChartClipPath>

  <ChartClipPath>
    <!-- Bolus markers -->
    {#if showBolus}
      {#each bolusMarkers as marker, i (markerKey(marker.treatmentId, marker.time, i))}
        {@const xPos = chartCtx.xScale(marker.time)}
        {@const yPos = markerBaselineY}
        <BolusMarker
          {xPos}
          {yPos}
          insulin={marker.insulin ?? 0}
          isOverride={marker.isOverride ?? false}
          bolusType={marker.bolusType}
          treatmentId={marker.treatmentId ?? ""}
          onMarkerClick={effectiveOnMarkerClick}
          showLabel={bolusLabelVisible.has(marker)}
          printed={ctx.printing}
        />
      {/each}
    {/if}

    <!-- Carb markers -->
    {#if showCarbs}
      {#each carbMarkers as marker, i (markerKey(carbMarkerKey(marker), marker.time, i))}
        {@const xPos = chartCtx.xScale(marker.time)}
        {@const yPos = markerBaselineY}
        <CarbMarker
          {xPos}
          {yPos}
          carbs={marker.carbs ?? 0}
          label={mealLabelVisible.has(marker) ? (carbMarkerName(marker) ?? null) : null}
          treatmentId={marker.treatmentId ?? ""}
          onMarkerClick={effectiveOnMarkerClick}
          showLabel={carbLabelVisible.has(marker)}
          printed={ctx.printing}
        />
      {/each}
    {/if}

    <!-- COB highlight with remapped scale (scaled by carb ratio) -->
    {#if showCob}
      <Highlight
        x={(d) => d.time}
        y={(d) => {
          const cob = findSeriesValue(cobData, d.time);
          if (!cob || cob.value <= 0) return null;
          return iobScale(cob.value / ratio);
        }}
        points={{ class: "fill-carbs" }}
        onPointClick={effectiveOnPointClick
          ? (_e, { data }) => effectiveOnPointClick(data.time)
          : undefined}
      />
    {/if}

    <!-- IOB highlight with remapped scale -->
    {#if showIob}
      <Highlight
        x={(d) => d.time}
        y={(d) => {
          const iob = findSeriesValue(iobData, d.time);
          if (!iob || iob.value <= 0) return null;
          return iobScale(iob.value);
        }}
        points={{ class: "fill-iob-basal" }}
        onPointClick={effectiveOnPointClick
          ? (_e, { data }) => effectiveOnPointClick(data.time)
          : undefined}
      />
    {/if}
  </ChartClipPath>
{/if}
