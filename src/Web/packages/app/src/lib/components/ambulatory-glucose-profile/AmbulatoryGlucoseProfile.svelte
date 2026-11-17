<script lang="ts">
  import { tenantChartThresholds } from "#lib/stores/realtime-store.svelte.js";
  import { AreaChart, Tooltip } from "layerchart";
  import { EmptyState } from "#lib/components/shared/index.js";
  import {
    glucoseUnits,
    timeFormat,
  } from "#lib/stores/appearance-store.svelte.js";
  import { convertToDisplayUnits, bg, bgLabel, bgRange } from "#lib/utils/formatting.js";
  import { CHART_TEXTURES, patternClass } from "#lib/components/charts/print/chart-print-patterns.js";
  import ChartKey from "#lib/components/charts/print/ChartKey.svelte";
  import { GLUCOSE_BANDS } from "#lib/components/reports/percentile-band-colors.js";
  import type { AveragedStats, GlycemicThresholds } from "#lib/api/index.js";
  import {
    formatHour as _formatHour,
    transformStats,
    type AgpDataPoint,
  } from "./agp-utils";

  let {
    averagedStats,
    thresholds,
  }: {
    averagedStats?: AveragedStats[];
    thresholds?: GlycemicThresholds;
  } = $props();

  // Use props directly - parent should provide pre-fetched stats
  const rawData = $derived(averagedStats ?? []);

  // Reactive unit-aware data transformation
  const units = $derived(glucoseUnits.current);
  const isMMOL = $derived(units === "mmol");

  const data = $derived(transformStats(rawData, units));

  // Midnight repeated at 24 so the profile spans the whole day instead of stopping at 11pm.
  const dayData = $derived.by(() => {
    const midnight = data.find((d) => d.hour === 0);
    return midnight ? [...data, { ...midnight, hour: 24 }] : data;
  });

  const HOUR_TICKS = [0, 3, 6, 9, 12, 15, 18, 21, 24];

  const tenantThresholds = tenantChartThresholds();

  // Dynamic Y-axis domain based on units
  const yDomain = $derived<[number, number]>([
    0,
    convertToDisplayUnits(tenantThresholds.current.glucoseYMax, units),
  ]);

  const targetRange = $derived(
    thresholds?.targetBottom != null && thresholds.targetTop != null
      ? { low: thresholds.targetBottom, high: thresholds.targetTop }
      : null
  );

  // Time format for X-axis labels
  const is24Hour = $derived(timeFormat.current === "24");
  const formatHour = $derived((hour: number) => _formatHour(hour, is24Hour));

  // Format an already-unit-converted glucose value for the tooltip
  const formatValue = $derived((v?: number | null) =>
    v == null ? "–" : isMMOL ? v.toFixed(1) : Math.round(v).toString()
  );

  const { outer: BAND_OUTER, inner: BAND_INNER, median: MEDIAN_COLOR } = GLUCOSE_BANDS;

  // Drawn over the bands, so each label wears a paper-coloured halo to stay legible where a band crosses it.
  const RULE_LABEL_CLASS = "text-xs fill-foreground stroke-background [stroke-width:3px] [paint-order:stroke]";
</script>

{#if rawData.length > 0}
<div class="flex h-full w-full flex-col">
  <div class="min-h-0 flex-1">
  <AreaChart
    data={dayData}
    x={(d) => d.hour}
    y={(d) => d.median}
    series={[
      {
        key: "p10",
        value: [
          (d: AgpDataPoint) => d.percentiles?.p25,
          (d: AgpDataPoint) => d.percentiles?.p10,
        ],
        color: BAND_OUTER,
        label: "P10",
        props: { class: patternClass("percentile-outer") },
      },
      {
        key: "p25",
        value: [
          (d: AgpDataPoint) => d.median,
          (d: AgpDataPoint) => d.percentiles?.p25,
        ],
        color: BAND_INNER,
        label: "P25",
        props: { class: patternClass("percentile-inner") },
      },
      {
        key: "median",
        value: [(d: AgpDataPoint) => d.median, (d: AgpDataPoint) => d.median],
        color: MEDIAN_COLOR,
        props: {
          line: { strokeWidth: 2.5 },
        },
        label: "Median",
      },
      {
        key: "percentiles.p75",
        value: [
          (d: AgpDataPoint) => d.median,
          (d: AgpDataPoint) => d.percentiles?.p75,
        ],
        color: BAND_INNER,
        label: "P75",
        props: { class: patternClass("percentile-inner") },
      },
      {
        key: "p90",
        value: [
          (d: AgpDataPoint) => d.percentiles?.p75,
          (d: AgpDataPoint) => d.percentiles?.p90,
        ],
        color: BAND_OUTER,
        label: "P90",
        props: { class: patternClass("percentile-outer") },
      },
    ]}
    xDomain={[0, 24]}
    {yDomain}
    seriesLayout="overlap"
    annotations={targetRange
      ? [
          {
            type: "range",
            layer: "below",
            y: [
              convertToDisplayUnits(targetRange.low, units),
              convertToDisplayUnits(targetRange.high, units),
            ],
            fill: CHART_TEXTURES["target-band"].color,
            class: patternClass("target-band"),
          },
          {
            type: "line",
            layer: "above",
            y: convertToDisplayUnits(targetRange.low, units),
            label: `Low ${bg(targetRange.low)}`,
            labelPlacement: "bottom-left",
            labelXOffset: 4,
            labelYOffset: 4,
            props: {
              label: { class: RULE_LABEL_CLASS },
              line: { class: "stroke-foreground", strokeWidth: 1, "stroke-dasharray": "5 3" },
            },
          },
          {
            type: "line",
            layer: "above",
            y: convertToDisplayUnits(targetRange.high, units),
            label: `High ${bg(targetRange.high)}`,
            labelPlacement: "top-left",
            labelXOffset: 4,
            labelYOffset: 4,
            props: {
              label: { class: RULE_LABEL_CLASS },
              line: { class: "stroke-foreground", strokeWidth: 1, "stroke-dasharray": "5 3" },
            },
          },
        ]
      : []}
    props={{
      area: { motion: { type: "tween", duration: 200 } },
      xAxis: {
        motion: { type: "tween", duration: 200 },
        ticks: HOUR_TICKS,
        format: formatHour,
        tickOcclusion: { priority: "start-end" },
      },
      tooltip: { context: { mode: "bisect-x" } },
    }}
    padding={{ top: 20, right: 20, bottom: 40, left: 20 }}
  >
    {#snippet tooltip({ context })}
      <Tooltip.Root {context}>
        {#snippet children({ data })}
          {@const d: AgpDataPoint & { hour: number } = data}
          <Tooltip.Header value={`${formatHour(d.hour)} · ${bgLabel()}`} />
          <Tooltip.List>
            <Tooltip.Item
              label="P90"
              value={formatValue(d.percentiles?.p90)}
              color={BAND_OUTER}
              valueAlign="right"
            />
            <Tooltip.Item
              label="P75"
              value={formatValue(d.percentiles?.p75)}
              color={BAND_INNER}
              valueAlign="right"
            />
            <Tooltip.Item
              label="Median"
              value={formatValue(d.median)}
              color={MEDIAN_COLOR}
              valueAlign="right"
            />
            <Tooltip.Item
              label="P25"
              value={formatValue(d.percentiles?.p25)}
              color={BAND_INNER}
              valueAlign="right"
            />
            <Tooltip.Item
              label="P10"
              value={formatValue(d.percentiles?.p10)}
              color={BAND_OUTER}
              valueAlign="right"
            />
          </Tooltip.List>
        {/snippet}
      </Tooltip.Root>
    {/snippet}
  </AreaChart>
  </div>
  <ChartKey
    class="pt-2"
    items={[
      { texture: "percentile-outer", label: "10–90%", color: BAND_OUTER },
      { texture: "percentile-inner", label: "25–75%", color: BAND_INNER },
      { texture: "percentile-median", label: "Median", color: MEDIAN_COLOR, shape: "line" },
      ...(targetRange
        ? [{ texture: "target-band" as const, label: `Consensus target range ${bgRange(targetRange.low, targetRange.high)}` }]
        : []),
    ]}
  />
</div>
{:else}
  <div class="flex h-full w-full items-center justify-center">
    <EmptyState
      art="report-pages"
      size="compact"
      title="No pattern data"
      body="Need more readings to show your typical day"
    />
  </div>
{/if}
