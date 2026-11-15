<!--
  A chart panel's tracks inside one layerchart chart: each track's band of the plot, its left and
  right value axes, the glucose limits on a glucose side, and each drawn layer as its mark. Each run
  of tracks along one axis (axisGroups) draws over its own horizontal scale and bottom axis; the first
  run's is the chart's own. It draws into whatever layer it sits in, the page's Svg or an image's
  Canvas, from the same geometry (layered-layout.ts); `paint` turns a colour expression into what
  that layer paints with. On the page it gives the point under the pointer (`focus`): the run of
  tracks it is over and that run's slot there, which the crosshair spans; `step` moves it from the
  keyboard.
-->
<script lang="ts">
  import { Axis, getChartContext, Group, Line, Text } from "layerchart";
  import { scaleLinear, scaleTime } from "d3-scale";
  import type { ChartLabels } from "../../labels.js";
  import type { TextureKey } from "../../print/patterns.js";
  import type { GradientStop, LineColorMode } from "../../view/glucose-gradient-stops.js";
  import { reportGlucoseStops } from "../glucose-scale.js";
  import type { ReportLayer, ReportPanel, ReportXKind } from "../types.js";
  import {
    axisGroups,
    drawnLayers,
    fromDomain,
    groupAt,
    groupSpan,
    isTimeAxis,
    nearestSlot,
    paintOrder,
    slots,
    sideScales,
    toDomain,
    trackBands,
    xExtent,
    xFieldOf,
    type PlotFocus,
    type SharedSides,
    type SideScale,
    type TrackBand,
  } from "./layered-layout.js";
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
    /** A tick's words on an axis along `kind`. */
    formatTick: (value: unknown, kind: ReportXKind) => string;
    /** Draw a rule at the slot under the pointer, on the page. */
    hover?: boolean;
    /** The run of tracks and its slot under the pointer, or null; set by the plot. */
    focus?: PlotFocus | null;
    /** Values each track's sides cover beyond their own, by track id, so small multiples share one scale. */
    valueExtents?: Record<string, SharedSides>;
    /** The paint of a texture's ink laid over a mark's own fill; none where the layer cannot draw one (a canvas). */
    texture?: (key: TextureKey) => string | undefined;
  }

  let {
    panel,
    labels,
    lineColorMode,
    paint,
    gradient,
    tickLabels,
    text,
    formatTick,
    hover = false,
    valueExtents,
    texture,
    focus = $bindable(null),
  }: Props = $props();

  const chart = getChartContext();
  const groups = $derived(axisGroups(panel));
  const bands = $derived(trackBands(panel.tracks, chart.height, undefined, panel.tracks.map((_, i) => groups.some((g) => g.first === i))));
  const high = $derived(panel.thresholds?.high ?? null);
  const scales = $derived(panel.tracks.map((track, i) => sideScales(track, bands[i], high, valueExtents?.[track.id])));

  /** Each group's horizontal scale over the plot's width: the chart's own for the first. */
  const xScales = $derived(
    groups.map((group, g) => {
      if (g === 0) return chart.xScale;
      const [from, to] = xExtent(group.panel);
      return isTimeAxis(group.kind)
        ? scaleTime().domain([new Date(Number(toDomain(group.kind, from))), new Date(Number(toDomain(group.kind, to)))]).range([0, chart.width])
        : scaleLinear().domain([from, to]).range([0, chart.width]);
    }),
  );
  /** Each track's group, by track index. */
  const groupOf = $derived(panel.tracks.map((_, i) => groups.findLastIndex((g) => g.first <= i)));
  const xOf = (t: number) => {
    const g = groupOf[t];
    const kind = groups[g].kind;
    const scale = xScales[g] as (value: Date | number) => unknown;
    return (raw: number) => Number(scale(toDomain(kind, raw)));
  };

  /** Each group's slots, the x values the pointer and the keyboard move between. */
  const groupSlots = $derived(groups.map((group) => slots(group.panel)));
  const pixelOf = (g: number, raw: number) => Number((xScales[g] as (value: Date | number) => unknown)(toDomain(groups[g].kind, raw)));

  /** The run the pointer is over and its slot there: the first run's from the tooltip's data, another's from the pointer. */
  const pointed = $derived.by((): PlotFocus | null => {
    const data = chart.tooltip.data as { raw?: number } | null;
    if (!hover || !data || groups.length === 0) return null;
    const g = groupAt(groups, bands, chart.tooltip.y - chart.padding.top);
    const raw =
      g === 0
        ? (data.raw ?? null)
        : nearestSlot(groupSlots[g], fromDomain(groups[g].kind, (xScales[g] as { invert: (px: number) => Date | number }).invert(chart.tooltip.x - chart.padding.left)));
    return raw === null ? null : { group: g, raw, x: pixelOf(g, raw) };
  });
  $effect(() => {
    focus = pointed;
  });

  /**
   * Moves the focus one slot along its run (`along`, -1 or 1), or to the next run up or down (`across`), and shows
   * the tooltip there, as the pointer would.
   */
  export function step(along: number, across = 0) {
    const from = pointed ?? { group: 0, raw: groupSlots[0]?.[0] ?? 0, x: 0 };
    const g = Math.min(groups.length - 1, Math.max(0, from.group + across));
    const xs = groupSlots[g];
    if (xs.length === 0) return;
    const at = g === from.group ? xs.indexOf(from.raw) : xs.indexOf(nearestSlot(xs, from.raw) ?? xs[0]);
    const next = pointed === null ? 0 : Math.min(xs.length - 1, Math.max(0, (at < 0 ? 0 : at) + along));
    const raw = xs[next];
    const span = groupSpan(groups, bands, g);
    const firstRaw = g === 0 ? raw : (nearestSlot(groupSlots[0], fromDomain(groups[0].kind, (chart.xScale as { invert: (px: number) => Date | number }).invert(pixelOf(g, raw)))) ?? raw);
    chart.tooltip.show({
      data: { raw: firstRaw, x: toDomain(groups[0].kind, firstRaw) },
      point: { x: pixelOf(g, raw) + chart.padding.left, y: (span.top + span.bottom) / 2 + chart.padding.top },
    });
  }

  /** Takes the tooltip away, as the pointer leaving the plot does. */
  export function clear() {
    chart.tooltip.hide();
  }

  /** Where each group's bottom axis sits: under its last track, the last group's at the chart's foot. */
  function axisY(g: number): number {
    if (g === groups.length - 1) return chart.height;
    const last = groups[g + 1].first - 1;
    return bands[last].bottom + AXIS_GAP;
  }
  const AXIS_GAP = 4;

  /** The axis a scatter, a lag or minutes from an event lies along, named beside it where the panel has more than one axis. */
  function caption(g: number): string | null {
    if (groups.length < 2 || (groups[g].kind !== "value" && groups[g].kind !== "lag" && groups[g].kind !== "offset")) return null;
    const first = groups[g].panel.tracks.flatMap((t) => drawnLayers(t))[0];
    const field = first?.fields.find((f) => f.name === xFieldOf(first));
    return field ? `${field.label}${field.unit ? ` (${field.unit})` : ""}` : null;
  }

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
  {@const x = xOf(t)}
  {#if track.title && !track.layers.every((l) => l.mark === "spans")}
    <Text x={6} y={t === 0 ? band.top + 12 : band.top - 5} value={track.title} fontSize={11} {...text.muted} />
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
  {#each paintOrder(track) as layer (layer.id)}
    {@const side = sideOf(t, layer)}
    {#if layer.mark === "spans"}
      <MarkSpans {layer} {band} {x} {paint} {text} laneX={-chart.padding.left + 6} />
    {:else if layer.mark === "markers"}
      <MarkMarkers {layer} {track} {band} {x} yOf={(l) => sideOf(t, l)?.toPixel ?? null} {paint} {labels} {text} />
    {:else if side}
      {#if layer.mark === "line"}
        <MarkLine {layer} {x} y={side.toPixel} {paint} glucoseStops={glucoseStops(side)} {gradient} plotHeight={chart.height} />
      {:else if layer.mark === "area"}
        <MarkArea {layer} {x} y={side.toPixel} {paint} {texture} />
      {:else if layer.mark === "band"}
        <MarkBand {layer} {x} y={side.toPixel} {paint} glucoseStops={glucoseStops(side)} {gradient} plotHeight={chart.height} {texture} />
      {:else if layer.mark === "bars"}
        <MarkBars {layer} {x} y={side.toPixel} {paint} plotWidth={chart.width} {texture} behind={drawnLayers(track).some((l) => l.mark === "line" || l.mark === "points" || l.mark === "band")} />
      {:else if layer.mark === "points"}
        <MarkPoints {layer} {x} y={side.toPixel} {paint} />
      {/if}
    {/if}
  {/each}
{/each}
{#if groups.length === 1}
  <Axis placement="bottom" format={(value) => formatTick(value, groups[0].kind)} tickOcclusion rule={false} tickLabelProps={tickLabels} />
{:else}
  {#each groups as group, g (group.first)}
    {@const words = caption(g)}
    <Group y={axisY(g) - chart.height}>
      <Axis
        placement="bottom"
        scale={xScales[g]}
        format={(value) => formatTick(value, group.kind)}
        tickOcclusion
        rule={false}
        tickLabelProps={tickLabels}
      />
    </Group>
    {#if words}
      <Text x={chart.width} y={axisY(g) - 4} value={words} textAnchor="end" fontSize={11} {...text.muted} />
    {/if}
  {/each}
{/if}
{#if hover && pointed}
  {@const span = groupSpan(groups, bands, pointed.group)}
  <Line x1={pointed.x} x2={pointed.x} y1={span.top} y2={span.bottom} class="stroke-muted-foreground/50" data-testid="report-crosshair" />
{/if}
