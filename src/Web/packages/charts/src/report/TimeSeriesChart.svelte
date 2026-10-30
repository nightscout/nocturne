<!--
  Lines of one unit over time from a report panel's rows, with an optional shaded band between two
  fields. A field on the glucose scale is coloured as the dashboard's glucose line is (lineStopsFor),
  in the report's one colour mode; every other line takes its field's theme colour, and a field that
  holds its value until the next row is drawn as steps. The panel's treatment markers (the API's) are
  drawn as the dashboard's markers on its first line, or at their amounts where every field is a
  marker field, which is not drawn as a line.
-->
<script lang="ts">
  import { Area, Axis, Chart, ChartClipPath, LinearGradient, Rule, Spline, Svg, Tooltip } from "layerchart";
  import { curveMonotoneX, curveStepAfter, scaleTime } from "d3";
  import { resolveChartColor } from "../colors.js";
  import { formatClockTime, type ChartFormat, type GlucoseUnits } from "../format.js";
  import type { ChartLabels } from "../labels.js";
  import ReportMarkerLayer, { type PlacedMarker } from "./ReportMarkerLayer.svelte";
  import { lineStopsFor, type LineColorMode } from "../view/glucose-gradient-stops.js";
  import {
    boundFields,
    formatMinuteOfDay,
    formatOffset,
    formatReportValue,
    type ReportPanel,
    type ReportRow,
    type ReportThresholds,
  } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
    /** The report's glucose colouring, from the API: the dashboard's spectrum by default. */
    lineColorMode?: Exclude<LineColorMode, "single">;
    /** The chart's words, which the treatment markers write their amounts with; no markers are drawn without them. */
    labels?: ChartLabels;
    glucoseUnits?: GlucoseUnits;
  }

  let {
    panel,
    format,
    heightClass = "h-[280px]",
    lineColorMode = "continuous",
    labels,
    glucoseUnits = "mg/dl",
  }: Props = $props();

  type Point = { time: Date; row: ReportRow };

  /**
   * Minutes of the day or from an event are laid on the time axis from a fixed origin, so one time
   * scale serves every axis and its ticks fall on whole hours; the labels convert them back.
   */
  const MINUTE_ORIGIN = Date.UTC(2000, 0, 1);
  const fromMinutes = (minutes: number) => new Date(MINUTE_ORIGIN + minutes * 60_000);
  const toMinutes = (date: Date) => (date.getTime() - MINUTE_ORIGIN) / 60_000;

  const xName = $derived(panel.bindings.x ?? "time");
  // A time of day or minutes from an event lie on a linear axis; an instant on a time axis.
  const xKind = $derived(panel.fields.find((f) => f.name === xName)?.kind ?? "time");
  const linear = $derived(xKind === "time_of_day" || xKind === "offset");
  const bound = $derived(boundFields(panel, panel.bindings.y));
  const lines = $derived(bound.filter((f) => !f.marker));
  const band = $derived(boundFields(panel, panel.bindings.band));
  const thresholds = $derived(lines.some((f) => f.glucoseScale) ? (panel.thresholds ?? null) : null);

  const points = $derived<Point[]>(
    panel.rows.flatMap((row) => {
      const at = row[xName];
      return typeof at === "number" ? [{ time: linear ? fromMinutes(at) : new Date(at), row }] : [];
    }),
  );

  function num(row: ReportRow, name: string): number | null {
    const value = row[name];
    return typeof value === "number" ? value : null;
  }

  function drawnFor(name: string): Point[] {
    return points.filter((p) => num(p.row, name) !== null);
  }

  const bandPoints = $derived(
    band.length === 2
      ? points.filter((p) => num(p.row, band[0].name) !== null && num(p.row, band[1].name) !== null)
      : [],
  );

  // The axis ceiling: the largest drawn value, or the high threshold when that is larger.
  const yMax = $derived.by(() => {
    const names = [...bound, ...band].map((f) => f.name);
    let max = thresholds?.high ?? 0;
    for (const p of points) for (const n of names) max = Math.max(max, num(p.row, n) ?? 0);
    return max > 0 ? max * 1.1 : 1;
  });

  // The stops are placed in mg/dL; the axis is in the display unit, which the API relates to mg/dL.
  function glucoseStops(t: ReportThresholds, yScale: (value: number) => number, height: number) {
    const f = t.mgdlPerUnit;
    const mgdl = { veryLow: t.veryLow * f, low: t.low * f, high: t.high * f, veryHigh: t.veryHigh * f };
    return lineStopsFor(lineColorMode, mgdl, (value) => yScale(value / f), height);
  }

  /**
   * Where each of the panel's treatment markers sits: on the first line at the reading nearest it, a
   * carb raised above a bolus at the same time; on a panel whose fields are all markers, at its own
   * amount. What a marker is (its kind, amount, whether the algorithm gave it) is the API's.
   */
  function placeMarkers(xScale: (d: Date) => number, yScale: (v: number) => number, height: number): PlacedMarker[] {
    const line = lines[0];
    const along = line ? drawnFor(line.name) : [];
    return (panel.markers ?? []).map((m, i) => {
      let y: number;
      if (line) {
        let nearest: Point | undefined;
        for (const p of along)
          if (!nearest || Math.abs(p.time.getTime() - m.time) < Math.abs(nearest.time.getTime() - m.time)) nearest = p;
        const value = nearest ? num(nearest.row, line.name) : null;
        const onLine = value === null ? height - 12 : yScale(value);
        y = m.kind === "carbs" ? onLine - 34 : onLine;
      } else {
        y = yScale(m.amount);
      }
      return {
        key: `marker-${m.kind}-${m.time}-${i}`,
        x: xScale(new Date(m.time)),
        y,
        kind: m.kind,
        amount: m.amount,
        automatic: m.automatic ?? false,
      };
    });
  }

  const lineColor = (color: string | null | undefined) =>
    color ? resolveChartColor(color) : "var(--foreground)";
  const time = (v: unknown) => {
    if (!(v instanceof Date)) return String(v);
    if (xKind === "time_of_day") return formatMinuteOfDay(toMinutes(v), format);
    if (xKind === "offset") return formatOffset(toMinutes(v), format);
    return formatClockTime(v, format);
  };
  const xDomain = $derived.by((): [Date, Date] => {
    if (!linear) return [new Date(panel.window.from), new Date(panel.window.to)];
    if (xKind === "time_of_day") return [fromMinutes(0), fromMinutes(1440)];
    const xs = points.map((p) => p.time.getTime());
    return [new Date(Math.min(...xs)), new Date(Math.max(...xs))];
  });
</script>

{#if points.length === 0}
  <p class="flex {heightClass} items-center justify-center text-sm text-muted-foreground">
    No data in this range.
  </p>
{:else}
  <div class="{heightClass} w-full" data-testid="report-time-series">
    <Chart
      data={points}
      x={(d: Point) => d.time}
      xScale={scaleTime()}
      {xDomain}
      yDomain={[0, yMax]}
      padding={{ left: 40, right: 12, top: 8, bottom: 24 }}
      tooltipContext={{ mode: "quadtree-x" }}
    >
      {#snippet children({ context })}
        <Svg>
          <Axis placement="left" ticks={5} tickLabelProps={{ class: "text-xs fill-muted-foreground" }} />
          <Axis
            placement="bottom"
            format={time}
            tickOcclusion
            tickLabelProps={{ class: "text-xs fill-muted-foreground", fontSize: 12 }}
          />
          <ChartClipPath>
            {#if bandPoints.length > 0}
              <Area
                data={bandPoints}
                x={(d: Point) => d.time}
                y0={(d: Point) => num(d.row, band[0].name)}
                y1={(d: Point) => num(d.row, band[1].name)}
                class="fill-glucose-in-range/15"
              />
            {/if}
            {#if thresholds}
              <Rule y={thresholds.low} class="stroke-glucose-low/60" dashArray="4 4" />
              <Rule y={thresholds.high} class="stroke-glucose-high/60" dashArray="4 4" />
            {/if}
            {#each lines as field (field.name)}
              {#if field.glucoseScale && thresholds}
                <LinearGradient
                  stops={glucoseStops(thresholds, context.yScale, context.height)}
                  units="userSpaceOnUse"
                  vertical
                  y1="0"
                  y2={String(context.height)}
                >
                  {#snippet children({ gradient })}
                    <Spline
                      data={drawnFor(field.name)}
                      x={(d: Point) => d.time}
                      y={(d: Point) => num(d.row, field.name)}
                      stroke={gradient}
                      class="stroke-2 fill-none"
                      curve={field.step ? curveStepAfter : curveMonotoneX}
                    />
                  {/snippet}
                </LinearGradient>
              {:else}
                <Spline
                  data={drawnFor(field.name)}
                  x={(d: Point) => d.time}
                  y={(d: Point) => num(d.row, field.name)}
                  stroke={lineColor(field.color)}
                  class="stroke-2 fill-none"
                  curve={field.step ? curveStepAfter : curveMonotoneX}
                />
              {/if}
            {/each}
            {#if labels && (panel.markers ?? []).length > 0}
              <ReportMarkerLayer
                markers={placeMarkers(context.xScale, context.yScale, context.height)}
                {labels}
                {format}
                {glucoseUnits}
              />
            {/if}
          </ChartClipPath>
        </Svg>

        <Tooltip.Root {context} class="z-50 rounded-lg border border-border text-xs shadow-xl">
          {#snippet children({ data })}
            <Tooltip.Header
              value={data?.time}
              format={time}
              class="mb-1 border-b border-border pb-1 font-semibold"
            />
            <Tooltip.List>
              {#each [...bound, ...band] as field (field.name)}
                <Tooltip.Item
                  label={field.label}
                  value={`${formatReportValue(data?.row?.[field.name], field, format)}${field.unit ? ` ${field.unit}` : ""}`}
                  color={lineColor(field.color)}
                />
              {/each}
            </Tooltip.List>
          {/snippet}
        </Tooltip.Root>
      {/snippet}
    </Chart>
  </div>
  {#if lines.length > 1}
    <ul class="mt-1 flex flex-wrap gap-3 text-xs text-muted-foreground">
      {#each lines as field (field.name)}
        <li class="flex items-center gap-1">
          <span class="inline-block h-0.5 w-3 bg-(--swatch)" style:--swatch={lineColor(field.color)}></span>
          {field.label}{field.unit ? ` (${field.unit})` : ""}
        </li>
      {/each}
    </ul>
  {/if}
{/if}
