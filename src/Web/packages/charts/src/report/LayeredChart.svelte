<!--
  A chart panel: its tracks one above another, each with up to two value axes, every layer drawn as
  its mark (LayeredPlot), one tooltip that lists each layer's row under the pointer, and a legend of
  the layers. Each run of tracks along one axis has its own horizontal axis; the tooltip reads each
  run at the pointer's position on it, under that run's own heading. Over categories each track's
  bars are drawn by CategoryBars. Every value, word and colour is the API's.
-->
<script lang="ts">
  import { Chart, Pattern, Svg, Tooltip } from "layerchart";
  import { scaleLinear, scaleTime } from "d3-scale";
  import type { ChartFormat } from "../format.js";
  import type { ChartLabels } from "../labels.js";
  import type { LineColorMode } from "../view/glucose-gradient-stops.js";
  import { inkPattern, inkPatternId, textureBackground, type InkPattern } from "../print/patterns.js";
  import CategoryBars from "./CategoryBars.svelte";
  import LayeredPlot from "./layered/LayeredPlot.svelte";
  import { formatAxisValue, formatPointerValue } from "./layered/axis-format.js";
  import {
    AXIS_ROOM,
    axisGroups,
    drawnLayers,
    fieldColor,
    isTimeAxis,
    legend,
    panelTextures,
    rawAcross,
    rowMatch,
    tipEntries,
    type TipEntry,
    rowsAt,
    type SharedSides,
    slots,
    toDomain,
    valueFields,
    xExtent,
  } from "./layered/layered-layout.js";
  import { formatReportValue, type ReportField, type ReportPanel, type ReportRow, type ReportXKind } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    /** The report's glucose colouring, from the API. */
    lineColorMode?: Exclude<LineColorMode, "single">;
    /** The chart's words, which treatment markers write their amounts with; no markers are drawn without them. */
    labels?: ChartLabels;
    /** The plot's height, in pixels, for one track of normal height; a chart of more tracks is taller. */
    trackHeight?: number;
    /** Draw the key of the layers under the plot; a host that names the layers itself leaves it out. */
    legend?: boolean;
    /** Values each track's sides cover beyond their own, by track id, so small multiples share one scale. */
    valueExtents?: Record<string, SharedSides>;
  }

  let { panel, format, lineColorMode = "continuous", labels, trackHeight = 240, legend: showLegend = true, valueExtents }: Props = $props();

  const groups = $derived(axisGroups(panel));
  const kind = $derived(groups[0]?.kind ?? panel.xKind ?? "time");
  const weight = $derived(panel.tracks.reduce((sum, t) => sum + t.height, 0));
  const axes = $derived(AXIS_ROOM * Math.max(0, groups.length - 1));
  const height = $derived(Math.round(Math.min(560, Math.max(trackHeight, trackHeight * 0.75 * weight + 40)) + axes));
  const hasRight = $derived(panel.tracks.some((t) => t.layers.some((l) => l.side === "right")));
  const extent = $derived(xExtent(groups[0]?.panel ?? panel));
  const points = $derived(slots(groups[0]?.panel ?? panel).map((raw) => ({ raw, x: toDomain(kind, raw) })));
  const anyRows = $derived(slots(panel).length > 0);
  /** Each group's match under the pointer: a row is the slot's within the widest gap between the group's slots. */
  const matches = $derived(
    groups.map((group) => {
      const xs = slots(group.panel);
      let gap = 0;
      for (let i = 1; i < xs.length; i++) gap = Math.max(gap, xs[i] - xs[i - 1]);
      return rowMatch(group.panel, gap || 1);
    }),
  );
  /** The ink-only textures the panel's series are hatched with; drawn on screen and on paper alike. */
  const inks = $derived(panelTextures(panel).map(inkPattern).filter((p): p is InkPattern => p !== null));
  const entries = $derived(legend(panel));
  const xField = $derived.by((): ReportField | undefined => {
    const first = panel.tracks.flatMap((t) => drawnLayers(t))[0];
    return first?.fields.find((f) => f.name === first.bindings.x);
  });

  const tick = (value: unknown, along: ReportXKind = kind) => formatAxisValue(along, value, format);
  const pointer = (value: unknown, along: ReportXKind = kind) => formatPointerValue(along, value, format);
  const valueText = (value: ReportRow[string], field: ReportField, unit: boolean) =>
    `${formatReportValue(value, field, format)}${unit && field.unit && value !== null && value !== undefined ? ` ${field.unit}` : ""}`;

  /** One axis group's part of the tooltip: its heading where the panel has more than one axis, and its entries. */
  interface TipSection {
    key: number;
    heading: string | null;
    entries: TipEntry[];
  }

  /** What the tooltip lists at the pointer: in each group, each drawn layer's entries at its rows there. */
  function sectionsAt(raw: number): TipSection[] {
    const raws = rawAcross(groups, raw);
    return groups.map((group, g) => ({
      key: group.first,
      heading: g > 0 ? pointer(toDomain(group.kind, raws[g]), group.kind) : null,
      entries: group.panel.tracks
        .flatMap((track) => drawnLayers(track))
        .flatMap((layer) => tipEntries(layer, rowsAt(layer, raws[g], matches[g]).slice(0, 4), valueText)),
    }));
  }
</script>

{#snippet plot()}
    <Svg>
      {#each inks as ink (ink.id)}
        <Pattern id={ink.id} size={ink.size} lines={ink.lines} circles={ink.circles} />
      {/each}
      <LayeredPlot
        {panel}
        {labels}
        {lineColorMode}
        paint={(css) => css}
        tickLabels={{ class: "text-xs fill-muted-foreground", fontSize: 11 }}
        text={{ muted: { class: "fill-muted-foreground" }, strong: { class: "fill-foreground font-semibold" } }}
        formatTick={(value, along) => tick(value, along)}
        {valueExtents}
        texture={(key) => `url(#${inkPatternId(key)})`}
        hover
      />
    </Svg>
    <Tooltip.Root class="z-50 max-w-96 rounded-lg border border-border text-xs shadow-xl">
      {#snippet children({ data })}
        <Tooltip.Header value={data?.x} format={(value) => pointer(value)} class="mb-1 border-b border-border pb-1 font-semibold" />
        {#each sectionsAt(data?.raw ?? 0) as section (section.key)}
          {#if section.heading}
            <p class="mt-2 mb-1 border-b border-border pb-1 font-semibold" data-testid="report-tooltip-axis">{section.heading}</p>
          {/if}
          <div class="grid grid-cols-[auto_1fr_auto] items-baseline gap-x-2 gap-y-1" data-testid="report-tooltip-entries">
            {#each section.entries as entry (entry.key)}
              <span class="size-2 self-center rounded-full bg-(--swatch)" style:--swatch={entry.color ?? "transparent"}></span>
              <span class="text-muted-foreground">{entry.label}</span>
              <span class="text-right whitespace-nowrap tabular-nums" data-testid="report-tooltip-value">
                <span class="font-medium text-foreground">{entry.value}</span>
                {#if entry.range}
                  <span class="ml-1 text-muted-foreground" data-testid="report-tooltip-range">{entry.range}</span>
                {/if}
              </span>
              {#each entry.notes as note, i (i)}
                <p class="col-span-2 col-start-2 text-pretty text-muted-foreground/80">{note}</p>
              {/each}
            {/each}
          </div>
        {/each}
      {/snippet}
    </Tooltip.Root>
{/snippet}

{#if kind === "category"}
  <div class="flex flex-col gap-4" data-testid="report-layered-chart">
    {#each panel.tracks as track (track.id)}
      {#each drawnLayers(track).filter((l) => l.mark === "bars") as layer (layer.id)}
        <CategoryBars {layer} {format} />
      {/each}
    {/each}
  </div>
{:else if !anyRows}
  <p class="flex h-[200px] items-center justify-center text-sm text-muted-foreground">No data in this range.</p>
{:else}
  <div class="h-(--plot-height) w-full" style:--plot-height="{height}px" data-testid="report-layered-chart" data-tracks={panel.tracks.length}>
    {#if isTimeAxis(kind)}
      <Chart
        data={points}
        x="x"
        xScale={scaleTime()}
        xDomain={[toDomain(kind, extent[0]), toDomain(kind, extent[1])]}
        yDomain={[0, 1]}
        padding={{ left: 52, right: hasRight ? 52 : 12, top: 8, bottom: 24 }}
        tooltipContext={{ mode: "bisect-x" }}
      >
        {@render plot()}
      </Chart>
    {:else}
      <Chart
        data={points}
        x="x"
        xScale={scaleLinear()}
        xDomain={[toDomain(kind, extent[0]), toDomain(kind, extent[1])]}
        yDomain={[0, 1]}
        padding={{ left: 52, right: hasRight ? 52 : 12, top: 8, bottom: 24 }}
        tooltipContext={{ mode: "bisect-x" }}
      >
        {@render plot()}
      </Chart>
    {/if}
  </div>
  {#if groups.length === 1 && (kind === "value" || kind === "lag") && xField}
    <p class="text-center text-xs text-muted-foreground" data-testid="report-x-caption">{xField.label}{xField.unit ? ` (${xField.unit})` : ""}</p>
  {/if}
  {#if showLegend && (entries.length > 1 || entries.some((e) => e.context))}
    <ul class="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground" data-testid="report-legend">
      {#each entries as entry (entry.id)}
        <li class={["flex flex-wrap items-center gap-x-1.5 gap-y-0.5", entry.parts.length > 0 && "basis-full"]}>
          {#if entry.parts.length > 0}
            <!-- A band's parts carry its swatches. -->
          {:else if entry.color && entry.texture}
            {@const hatch = textureBackground(entry.texture)}
            <span
              class="inline-block size-3 rounded-xs bg-(--swatch) bg-(image:--hatch) bg-size-(--hatch-size)"
              style:--swatch={entry.color}
              style:--hatch={hatch?.image ?? "none"}
              style:--hatch-size={hatch?.size ?? "auto"}
              data-texture={entry.texture}
            ></span>
          {:else if entry.color}
            <span class="inline-block h-0.5 w-3 bg-(--swatch)" style:--swatch={entry.color}></span>
          {/if}
          {entry.title}{entry.unit ? ` (${entry.unit})` : ""}
          {#if entry.context}
            <span class="text-muted-foreground/80" data-testid="report-legend-context">· {entry.context}</span>
          {/if}
          {#each entry.parts as part (part.key)}
            <span class="ml-2 inline-flex items-center gap-1 text-muted-foreground/80" data-testid="report-legend-part" data-shape={part.shape}>
              {#if part.shape === "fill"}
                {@const hatch = entry.texture ? textureBackground(entry.texture) : null}
                <span
                  class="inline-block size-3 rounded-xs bg-(--swatch) bg-(image:--hatch) bg-size-(--hatch-size)"
                  style:--swatch={part.color ?? "transparent"}
                  style:--hatch={hatch?.image ?? "none"}
                  style:--hatch-size={hatch?.size ?? "auto"}
                ></span>
              {:else if part.color}
                <span class="inline-block h-0.5 w-3 bg-(--swatch)" style:--swatch={part.color}></span>
              {:else}
                <!-- A middle line coloured by glucose: its swatch runs through the glucose colours. -->
                <span
                  class="inline-block h-0.5 w-3 bg-linear-to-r from-glucose-low via-glucose-in-range to-glucose-high"
                  data-testid="report-legend-glucose-swatch"
                ></span>
              {/if}
              {part.label}
            </span>
          {/each}
        </li>
      {/each}
    </ul>
  {/if}
{/if}
