<!--
  Lines of one unit over time from a report panel's rows, with an optional shaded band between two
  fields. A field on the glucose scale is coloured by the panel's thresholds, as the dashboard's
  glucose line is (lineStopsFor); every other line takes its field's theme colour.
-->
<script lang="ts">
  import { Area, Axis, Chart, ChartClipPath, LinearGradient, Rule, Spline, Svg, Tooltip } from "layerchart";
  import { curveMonotoneX, scaleTime } from "d3";
  import { resolveChartColor } from "../colors.js";
  import { formatClockTime, type ChartFormat } from "../format.js";
  import { lineStopsFor } from "../view/glucose-gradient-stops.js";
  import { boundFields, formatReportValue, type ReportPanel, type ReportRow } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
  }

  let { panel, format, heightClass = "h-[280px]" }: Props = $props();

  type Point = { time: Date; row: ReportRow };

  const xName = $derived(panel.bindings.x ?? "time");
  const lines = $derived(boundFields(panel, panel.bindings.y));
  const band = $derived(boundFields(panel, panel.bindings.band));
  const thresholds = $derived(lines.some((f) => f.glucoseScale) ? (panel.thresholds ?? null) : null);

  const points = $derived<Point[]>(
    panel.rows.flatMap((row) => {
      const at = row[xName];
      return typeof at === "number" ? [{ time: new Date(at), row }] : [];
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
    const names = [...lines, ...band].map((f) => f.name);
    let max = thresholds?.high ?? 0;
    for (const p of points) for (const n of names) max = Math.max(max, num(p.row, n) ?? 0);
    return max > 0 ? max * 1.1 : 1;
  });

  const lineColor = (color: string | null | undefined) =>
    color ? resolveChartColor(color) : "var(--foreground)";
  const time = (v: unknown) => (v instanceof Date ? formatClockTime(v, format) : String(v));
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
      xDomain={[new Date(panel.window.from), new Date(panel.window.to)]}
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
                  stops={lineStopsFor("threshold", thresholds, context.yScale, context.height)}
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
                      curve={curveMonotoneX}
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
                  curve={curveMonotoneX}
                />
              {/if}
            {/each}
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
              {#each [...lines, ...band] as field (field.name)}
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
