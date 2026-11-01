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
  import { drawnLayers, isTimeAxis, legend, slots, toDomain, xExtent } from "../layered/layered-layout.js";
  import type { ReportPanel } from "../types.js";
  import { mergedBars } from "./static-panel.js";
  import StaticFrame, { staticPadding, type StaticKeyItem } from "./StaticFrame.svelte";

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
  const kind = $derived(panel.xKind ?? "time");
  const extent = $derived(xExtent(panel));
  const points = $derived(slots(panel).map((raw) => ({ raw, x: toDomain(kind, raw) })));
  const hasRight = $derived(panel.tracks.some((t) => t.layers.some((l) => l.side === "right")));
  const key = $derived<StaticKeyItem[]>(
    legend(panel).length > 1 || legend(panel).some((e) => e.context)
      ? legend(panel).map((entry) => ({
          label: `${entry.title}${entry.unit ? ` (${entry.unit})` : ""}${entry.context ? ` · ${entry.context}` : ""}`,
          color: entry.color ? paint(theme, entry.color) : null,
          shape: "line",
        }))
      : [],
  );
  const padding = $derived(staticPadding({ key: key.length > 0, left: 60, right: hasRight ? 56 : 20 }));
  const bars = $derived(mergedBars(panel.tracks.flatMap((t) => drawnLayers(t)).filter((l) => l.mark === "bars")));
  /** The axis a scatter or a lag lies along, named where the panel has no range line: values and lags are not times. */
  const caption = $derived.by(() => {
    if (kind !== "value" && kind !== "lag") return null;
    const first = panel.tracks.flatMap((t) => drawnLayers(t))[0];
    const field = first?.fields.find((f) => f.name === first.bindings.x);
    return field ? `${field.label}${field.unit ? ` (${field.unit})` : ""}` : null;
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
        formatTick={(value) => formatAxisValue(kind, value, format)}
      />
    </Canvas>
  </Chart>
{/if}
