<script lang="ts">
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import { AreaChart, Tooltip } from "layerchart";
  import { BarChart3 } from "lucide-svelte";
  import {
    glucoseUnits,
    timeFormat,
  } from "$lib/stores/appearance-store.svelte";
  import { convertToDisplayUnits, bgLabel } from "$lib/utils/formatting";
  import type { AveragedStats } from "$lib/api";
  import {
    formatHour as _formatHour,
    transformStats,
    AGP_LOW_THRESHOLD,
  } from "./agp-utils";

  let {
    averagedStats,
  }: {
    averagedStats?: AveragedStats[];
  } = $props();

  // Use props directly - parent should provide pre-fetched stats
  const rawData = $derived(averagedStats ?? []);

  // Reactive unit-aware data transformation
  const units = $derived(glucoseUnits.current);
  const isMMOL = $derived(units === "mmol");

  const data = $derived(transformStats(rawData, units));

  // Dynamic Y-axis domain based on units
  const yDomain = $derived<[number, number]>(isMMOL ? [0, 22.2] : [0, 400]);

  // Convert threshold values to display units
  const lowThreshold = $derived(
    convertToDisplayUnits(AGP_LOW_THRESHOLD, units)
  );
  const highThreshold = $derived(
    convertToDisplayUnits(DEFAULT_THRESHOLDS.high ?? 180, units)
  );

  // Time format for X-axis labels
  const is24Hour = $derived(timeFormat.current === "24");
  const formatHour = $derived((hour: number) => _formatHour(hour, is24Hour));

  // Format an already-unit-converted glucose value for the tooltip
  const formatValue = $derived((v?: number | null) =>
    v == null ? "–" : isMMOL ? v.toFixed(1) : Math.round(v).toString()
  );

  // Band colors shared between the series fills and the tooltip swatches
  const BAND_OUTER = "oklch(from var(--chart-1) l c h / 0.35)";
  const BAND_INNER = "oklch(from var(--chart-1) l c h / 0.6)";
  const MEDIAN_COLOR = "var(--chart-1)";
</script>

{#if rawData.length > 0}
  <AreaChart
    {data}
    x={(d) => d.hour}
    y={(d) => d.median}
    renderContext="svg"
    legend
    series={[
      {
        key: "p10",
        value: [(d) => d.percentiles?.p25, (d) => d.percentiles?.p10],
        color: BAND_OUTER,
        label: "P10",
      },
      {
        key: "p25",
        value: [(d) => d.median, (d) => d.percentiles?.p25],
        color: BAND_INNER,
        label: "P25",
      },
      {
        key: "median",
        value: [(d) => d.median, (d) => d.median],
        color: MEDIAN_COLOR,
        props: {
          line: { strokeWidth: 1.75 },
        },
        label: "Median",
      },
      {
        key: "percentiles.p75",
        value: [(d) => d.median, (d) => d.percentiles?.p75],
        color: BAND_INNER,
        label: "P75",
      },
      {
        key: "p90",
        value: [(d) => d.percentiles?.p75, (d) => d.percentiles?.p90],
        color: BAND_OUTER,
        label: "P90",
      },
    ]}
    xDomain={[0, 23]}
    {yDomain}
    seriesLayout="overlap"
    annotations={[
      {
        type: "line",
        x: 0,
        y: lowThreshold,
        label: "Low",
        labelXOffset: 4,
        labelYOffset: 4,
        props: {
          label: {
            class: "text-xs text-muted-foreground",
          },
          line: {
            stroke: "var(--glucose-low)",
            strokeWidth: 1,
            "stroke-dasharray": "4 2",
          },
        },
      },
      {
        type: "line",
        x: 0,
        y: highThreshold,
        label: "High",
        labelXOffset: 4,
        labelYOffset: -12,
        props: {
          label: {
            class: "text-xs text-muted-foreground",
          },
          line: {
            stroke: "var(--glucose-high)",
            strokeWidth: 1,
            "stroke-dasharray": "4 2",
          },
        },
      },
    ]}
    props={{
      area: { motion: { type: "tween", duration: 200 } },
      xAxis: {
        motion: { type: "tween", duration: 200 },
        tickMultiline: true,
        format: formatHour,
      },
      tooltip: { context: { mode: "bisect-x" } },
    }}
    padding={{ top: 20, right: 20, bottom: 40, left: 20 }}
  >
    {#snippet tooltip({ context })}
      <Tooltip.Root {context}>
        {#snippet children({ data })}
          {@const d = data as (typeof data) & {
            hour: number;
            median: number;
            percentiles?: {
              p10: number;
              p25: number;
              p75: number;
              p90: number;
            };
          }}
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
{:else}
  <div
    class="flex h-full w-full items-center justify-center text-muted-foreground"
  >
    <div class="text-center">
      <BarChart3 class="mx-auto h-10 w-10 opacity-30" />
      <p class="mt-2 font-medium">No pattern data</p>
      <p class="text-sm">Need more readings to show your typical day</p>
    </div>
  </div>
{/if}
