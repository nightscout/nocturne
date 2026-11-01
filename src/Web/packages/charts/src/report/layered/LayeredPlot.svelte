<!--
  A chart panel's tracks inside one layerchart chart: each track's band of the plot, its left and
  right value axes, the glucose limits on a glucose side, and each drawn layer as its mark. It draws
  into whatever layer it sits in, the page's Svg or an image's Canvas, from the same geometry
  (layered-layout.ts); `paint` turns a colour expression into what that layer paints with.
-->
<script lang="ts">
  import { Axis, getChartContext, Line, Rule, Text } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import type { ChartLabels } from "../../labels.js";
  import type { GradientStop, LineColorMode } from "../../view/glucose-gradient-stops.js";
  import { reportGlucoseStops } from "../glucose-scale.js";
  import type { ReportLayer, ReportPanel } from "../types.js";
  import { drawnLayers, sideScales, toDomain, trackBands, type SideScale, type TrackBand } from "./layered-layout.js";
  import MarkArea from "../marks/MarkArea.svelte";
  import MarkBand from "../marks/MarkBand.svelte";
  import MarkBars from "../marks/MarkBars.svelte";
  import MarkLine from "../marks/MarkLine.svelte";
  import MarkMarkers from "../marks/MarkMarkers.svelte";
  import MarkPoints from "../marks/MarkPoints.svelte";
  import MarkSpans from "../marks/MarkSpans.svelte";
  import type { MarkText } from "../marks/mark-text.js";

  interface Props {
    panel: ReportPanel;
    labels?: ChartLabels;
    lineColorMode: Exclude<LineColorMode, "single">;
    /** The colour a mark paints with for a colour expression: the expression itself on the page, a theme colour on a canvas. */
    paint: (css: string) => string;
    /** A canvas's gradient stroke for a line coloured by glucose; none on the page, which draws an SVG gradient. */
    gradient?: (stops: GradientStop[], height: number) => string | undefined;
    tickLabels: Record<string, unknown>;
    text: MarkText;
    formatTick: (value: unknown) => string;
    /** Draw a rule at the slot under the pointer, on the page. */
    hover?: boolean;
  }

  let { panel, labels, lineColorMode, paint, gradient, tickLabels, text, formatTick, hover = false }: Props = $props();

  const chart = getChartContext();
  const kind = $derived(panel.xKind ?? "time");
  const bands = $derived(trackBands(panel.tracks, chart.height));
  const high = $derived(panel.thresholds?.high ?? null);
  const scales = $derived(panel.tracks.map((track, i) => sideScales(track, bands[i], high)));

  const x = (raw: number) => Number(chart.xScale(toDomain(kind, raw)));

  function sideOf(t: number, layer: ReportLayer): SideScale | null {
    return layer.side === "right" ? scales[t].right : scales[t].left;
  }

  /** Stops that colour a glucose line on its side's scale, over the whole plot's height. */
  function glucoseStops(side: SideScale | null): GradientStop[] | null {
    if (!side || !panel.thresholds) return null;
    return reportGlucoseStops(panel.thresholds, lineColorMode, side.toPixel, chart.height);
  }

  const axisScale = (side: SideScale, band: TrackBand) => scaleLinear().domain(side.domain).range([band.bottom, band.top]);

  /** Whether a side of a track holds a glucose field, so the glucose limits are drawn across it. */
  function glucoseSide(t: number, right: boolean): boolean {
    return drawnLayers(panel.tracks[t]).some((l) => (l.side === "right") === right && l.fields.some((f) => f.glucoseScale));
  }

  const LIMITS = [
    "color-mix(in srgb, var(--glucose-low) 60%, var(--background))",
    "color-mix(in srgb, var(--glucose-high) 60%, var(--background))",
  ];
</script>

{#each panel.tracks as track, t (track.id)}
  {@const band = bands[t]}
  {@const sides = scales[t]}
  {#if track.title && !track.layers.every((l) => l.mark === "spans")}
    <Text x={6} y={band.top + 12} value={track.title} fontSize={11} {...text.muted} />
  {/if}
  {#if sides.left}
    <Axis placement="left" scale={axisScale(sides.left, band)} ticks={band.height < 80 ? 2 : 4} rule={false} tickLabelProps={tickLabels} />
  {/if}
  {#if sides.right}
    <Axis placement="right" scale={axisScale(sides.right, band)} ticks={band.height < 80 ? 2 : 4} rule={false} tickLabelProps={tickLabels} />
  {/if}
  {#if panel.thresholds}
    {#each [sides.left, sides.right] as side, s (s)}
      {#if side && glucoseSide(t, s === 1)}
        {#each [panel.thresholds.low, panel.thresholds.high] as limit, k (k)}
          {#if limit >= side.domain[0] && limit <= side.domain[1]}
            <Line
              x1={0}
              x2={chart.width}
              y1={side.toPixel(limit)}
              y2={side.toPixel(limit)}
              stroke={paint(LIMITS[k])}
              strokeWidth={1}
              dashArray="4 4"
            />
          {/if}
        {/each}
      {/if}
    {/each}
  {/if}
  {#each drawnLayers(track) as layer (layer.id)}
    {@const side = sideOf(t, layer)}
    {#if layer.mark === "spans"}
      <MarkSpans {layer} {band} {x} {paint} {text} laneX={-chart.padding.left + 6} />
    {:else if layer.mark === "markers"}
      <MarkMarkers {layer} {track} {band} {x} yOf={(l) => sideOf(t, l)?.toPixel ?? null} {paint} {labels} {text} />
    {:else if side}
      {#if layer.mark === "line"}
        <MarkLine {layer} {x} y={side.toPixel} {paint} glucoseStops={glucoseStops(side)} {gradient} plotHeight={chart.height} />
      {:else if layer.mark === "area"}
        <MarkArea {layer} {x} y={side.toPixel} {paint} />
      {:else if layer.mark === "band"}
        <MarkBand {layer} {x} y={side.toPixel} {paint} glucoseStops={glucoseStops(side)} {gradient} plotHeight={chart.height} />
      {:else if layer.mark === "bars"}
        <MarkBars {layer} {x} y={side.toPixel} {paint} plotWidth={chart.width} />
      {:else if layer.mark === "points"}
        <MarkPoints {layer} {x} y={side.toPixel} {paint} />
      {/if}
    {/if}
  {/each}
{/each}
<Axis placement="bottom" format={formatTick} tickOcclusion rule={false} tickLabelProps={tickLabels} />
{#if hover && chart.tooltip.data}
  <Rule x={chart.tooltip.data.x} class="stroke-muted-foreground/50" />
{/if}
