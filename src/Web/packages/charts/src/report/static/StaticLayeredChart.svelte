<!--
  LayeredChart's static mode: the same tracks, axes and marks (LayeredPlot, one geometry for both),
  painted on a canvas in the theme's colours under the panel's title and range line, with the layers
  in a key. Over categories it is every bars layer side by side, drawn as CategoryBars draws it.
-->
<script lang="ts">
  import { Canvas, Chart } from "layerchart";
  import { scaleLinear, scaleTime } from "d3-scale";
  import type { ChartFormat } from "../../format.js";
  import type { ChartLabels } from "../../labels.js";
  import { gradientStroke, paint, staticText, type StaticRender } from "../../theme/static-theme.js";
  import type { GradientStop, LineColorMode } from "../../view/glucose-gradient-stops.js";
  import CategoryBars from "../CategoryBars.svelte";
  import LayeredPlot from "../layered/LayeredPlot.svelte";
  import { formatAxisValue } from "../layered/axis-format.js";
  import { axisGroups, drawnLayers, isTimeAxis, legend, slots, toDomain, xExtent, xFieldOf } from "../layered/layered-layout.js";
  import type { ReportPanel } from "../types.js";
  import { mergedBars } from "./static-panel.js";
  import StaticFrame, { staticKeyRows, staticPadding, type StaticKeyItem } from "./StaticFrame.svelte";

  interface Props {
    still: StaticRender;
    panel: ReportPanel;
    format: ChartFormat;
    lineColorMode: Exclude<LineColorMode, "single">;
    labels?: ChartLabels;
    title: string;
  }

  let { still, panel, format, lineColorMode, labels, title }: Props = $props();

  const theme = $derived(still.theme);
  const text = $derived(staticText(theme));
  const groups = $derived(axisGroups(panel));
  const kind = $derived(groups[0]?.kind ?? panel.xKind ?? "time");
  const extent = $derived(xExtent(groups[0]?.panel ?? panel));
  const points = $derived(slots(groups[0]?.panel ?? panel).map((raw) => ({ raw, x: toDomain(kind, raw) })));
  const hasRight = $derived(panel.tracks.some((t) => t.layers.some((l) => l.side === "right")));
  const key = $derived<StaticKeyItem[]>(
    legend(panel).length > 1 || legend(panel).some((e) => e.context)
      ? legend(panel).flatMap((entry): StaticKeyItem[] => [
          {
            label: `${entry.title}${entry.unit ? ` (${entry.unit})` : ""}${entry.context ? ` · ${entry.context}` : ""}`,
            color: entry.color && entry.parts.length === 0 ? paint(theme, entry.color) : null,
            shape: "line",
          },
          ...entry.parts.map((part): StaticKeyItem => ({
            label: part.label,
            color: part.color ? paint(theme, part.color) : null,
            shape: part.shape === "fill" ? "square" : "line",
          })),
        ])
      : [],
  );
  const padding = $derived(staticPadding({ key: staticKeyRows(key, still.width), left: 60, right: hasRight ? 56 : 20 }));
  const bars = $derived(mergedBars(panel.tracks.flatMap((t) => drawnLayers(t)).filter((l) => l.mark === "bars")));
  /**
   * The axis a scatter, a lag or minutes from an event lies along, named where the panel has no range
   * line, as LayeredChart names it: values, lags and offsets are not times.
   */
  const caption = $derived.by(() => {
    if (groups.length > 1 || (kind !== "value" && kind !== "lag" && kind !== "offset")) return null;
    const first = panel.tracks.flatMap((t) => drawnLayers(t))[0];
    const field = first?.fields.find((f) => f.name === xFieldOf(first));
    return field ? `${field.label}${field.unit && kind !== "offset" ? ` (${field.unit})` : ""}` : null;
  });
  const gradient = (stops: GradientStop[], height: number) => (still.gradient ? gradientStroke(still.gradient(0, height, stops)) : undefined);
</script>

{#if kind === "category" && bars}
  <CategoryBars layer={bars} {format} static={still} {title} context={panel.context} />
{:else}
  <Chart
    ssr
    width={still.width}
    height={still.height}
    data={points}
    x="x"
    xScale={isTimeAxis(kind) ? scaleTime() : scaleLinear()}
    xDomain={[toDomain(kind, extent[0]), toDomain(kind, extent[1])]}
    yDomain={[0, 1]}
    {padding}
  >
    <Canvas ssrCapture={still.capture}>
      <StaticFrame {theme} {title} context={panel.context ?? caption} {key} />
      <LayeredPlot
        {panel}
        {labels}
        {lineColorMode}
        paint={(css) => paint(theme, css)}
        {gradient}
        tickLabels={{ fill: text.muted, fontSize: 12, style: text.font }}
        text={{ muted: { fill: text.muted, style: text.font }, strong: { fill: text.foreground, style: text.strong } }}
        formatTick={(value, along) => formatAxisValue(along, value, format)}
      />
    </Canvas>
  </Chart>
{/if}
