<!--
  TimeSeriesChart's static mode: the same lines, band, limits and treatment markers, painted on a
  canvas in the theme's colours. Everything it draws the time series has already worked out.
-->
<script lang="ts" module>
  import type { ReportField, ReportRow } from "../types.js";
  import type { PlacedMarker } from "../ReportMarkerLayer.svelte";

  export interface StaticTimeSeriesModel {
    points: { time: Date; row: ReportRow }[];
    lines: ReportField[];
    band: ReportField[];
    bandPoints: { time: Date; row: ReportRow }[];
    thresholds: import("../types.js").ReportThresholds | null;
    yMax: number;
    xDomain: [Date, Date];
    formatTick: (value: unknown) => string;
    drawnFor: (name: string) => { time: Date; row: ReportRow }[];
    placeMarkers: (xScale: (d: Date) => number, yScale: (v: number) => number, height: number) => PlacedMarker[];
  }
</script>

<script lang="ts">
  import { Area, Axis, Canvas, Chart, Path, Rule, Text } from "layerchart";
  import { curveMonotoneX, curveStepAfter, scaleTime } from "d3";
  import { resolveChartColor } from "../../colors.js";
  import type { ChartLabels } from "../../labels.js";
  import { gradientStroke, paint, staticText, type StaticRender } from "../../theme/static-theme.js";
  import type { LineColorMode } from "../../view/glucose-gradient-stops.js";
  import { reportGlucoseStops } from "../glucose-scale.js";
  import StaticFrame, { staticPadding, type StaticKeyItem } from "./StaticFrame.svelte";
  import { linePath, markerPath } from "./static-marks.js";

  interface Props {
    still: StaticRender;
    model: StaticTimeSeriesModel;
    title: string;
    context?: string | null;
    lineColorMode: Exclude<LineColorMode, "single">;
    labels?: ChartLabels;
  }

  let { still, model, title, context = null, lineColorMode, labels }: Props = $props();

  const theme = $derived(still.theme);
  const text = $derived(staticText(theme));
  function num(row: ReportRow, name: string): number | null {
    const value = row[name];
    return typeof value === "number" ? value : null;
  }
  const colorOf = (field: ReportField) => paint(theme, field.color ? resolveChartColor(field.color) : "var(--foreground)");
  const key = $derived<StaticKeyItem[]>(
    model.lines.length > 1
      ? model.lines.map((field) => ({ label: `${field.label}${field.unit ? ` (${field.unit})` : ""}`, color: colorOf(field), shape: "line" }))
      : [],
  );
  const padding = $derived(staticPadding({ key: key.length > 0 }));

  function glucoseStroke(yScale: (v: number) => number, height: number): string {
    if (!model.thresholds) return paint(theme, "var(--glucose-in-range)");
    const stops = reportGlucoseStops(model.thresholds, lineColorMode, yScale, height).map(
      ([offset, color]): [number, string] => [offset, paint(theme, color)],
    );
    const gradient = still.gradient?.(0, height, stops);
    return gradient ? gradientStroke(gradient) : paint(theme, "var(--glucose-in-range)");
  }
</script>

<Chart
  ssr
  width={still.width}
  height={still.height}
  data={model.points}
  x={(d: { time: Date }) => d.time}
  xScale={scaleTime()}
  xDomain={model.xDomain}
  yDomain={[0, model.yMax]}
  {padding}
>
  {#snippet children({ context: chart })}
    <Canvas ssrCapture={still.capture}>
      <StaticFrame {theme} {title} {context} {key} />
      <Axis
        placement="left"
        ticks={5}
        grid={{ stroke: text.border }}
        rule={false}
        tickLabelProps={{ fill: text.muted, fontSize: 12, style: text.font }}
      />
      <Axis
        placement="bottom"
        format={model.formatTick}
        rule={{ stroke: text.border }}
        tickLabelProps={{ fill: text.muted, fontSize: 12, style: text.font }}
      />
      {#if model.bandPoints.length > 0}
        <Area
          data={model.bandPoints}
          x={(d: { time: Date }) => d.time}
          y0={(d: { row: ReportRow }) => num(d.row, model.band[0].name)}
          y1={(d: { row: ReportRow }) => num(d.row, model.band[1].name)}
          fill={paint(theme, "var(--glucose-in-range)")}
          fillOpacity={0.15}
          stroke="none"
        />
      {/if}
      {#if model.thresholds}
        <Rule y={model.thresholds.low} stroke={paint(theme, "var(--glucose-low)", undefined, 0.6)} dashArray="4 4" />
        <Rule y={model.thresholds.high} stroke={paint(theme, "var(--glucose-high)", undefined, 0.6)} dashArray="4 4" />
      {/if}
      {#each model.lines as field (field.name)}
        <Path
          pathData={linePath(
            model.drawnFor(field.name),
            (d) => chart.xScale(d.time),
            (d) => {
              const value = num(d.row, field.name);
              return value === null ? null : chart.yScale(value);
            },
            field.step ? curveStepAfter : curveMonotoneX,
          )}
          stroke={field.glucoseScale && model.thresholds ? glucoseStroke(chart.yScale, chart.height) : colorOf(field)}
          strokeWidth={2}
          fill="none"
        />
      {/each}
      {#if labels}
        {#each model.placeMarkers(chart.xScale, chart.yScale, chart.height) as marker (marker.key)}
          {@const color = paint(theme, marker.kind === "bolus" ? "var(--insulin-bolus)" : "var(--carbs)")}
          <Path
            pathData={markerPath(marker.kind, marker.x, marker.y)}
            fill={marker.automatic ? "none" : color}
            stroke={marker.automatic ? color : "none"}
            strokeWidth={1.5}
            opacity={0.9}
          />
          <Text
            x={marker.x}
            y={marker.kind === "bolus" ? marker.y + 20 : marker.y - 11}
            value={marker.kind === "bolus" ? labels.insulinAmount(marker.amount.toFixed(1)) : labels.carbAmount(String(marker.amount))}
            textAnchor="middle"
            fill={color}
            fontSize={10}
            style={text.strong}
          />
        {/each}
      {/if}
    </Canvas>
  {/snippet}
</Chart>
