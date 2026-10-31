<!--
  Percentile bands under a median line from a report panel's rows, one set per value of the series
  field, over minutes from an event or the time of day. Rows are laid out in slots by their x
  value, so every series shares one axis; a missing value is a gap. A row's numeric `cohort` field
  picks its colour, so a cohort keeps its colour when another is switched off; a row whose `range`
  field is "comparison" is drawn as its cohort's dashed overlay, not as a set of its own.

  A median on the glucose scale gets the glucose chart's treatment from the panel's thresholds: the
  target range shaded, the low and high limits drawn, and, for a single series, the median coloured
  by range as the time series colours glucose. Several cohorts keep their own hues, so they stay
  apart. Hovering a slot shows each series' median and middle spread; picking it hands `oncontext`
  the API's sentence for each series at that slot. `traces` (a glucose chart of event cohorts) are
  single events' glucose, drawn faintly in their cohort's hue under the bands.
-->
<script lang="ts">
  import { Axis, Chart, LinearGradient, Rect, Rule, Spline, Svg, Tooltip } from "layerchart";
  import PercentileBands from "./PercentileBands.svelte";
  import { cohortBands, GLUCOSE_BANDS, type BandColors, type PercentileAggregate } from "./percentile-bands.js";
  import { reportGlucoseStops } from "./glucose-scale.js";
  import type { LineColorMode } from "../view/glucose-gradient-stops.js";
  import {
    boundFields,
    formatMinuteOfDay,
    formatOffset,
    formatReportValue,
    rowSentence,
    type ReportField,
    type ReportPanel,
    type ReportRow,
    type ReportTrace,
  } from "./types.js";
  import type { ChartFormat } from "../format.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
    /** The report's glucose colouring, from the API. */
    lineColorMode?: Exclude<LineColorMode, "single">;
    /** Called with the API's sentences for the picked slot; slots are not picked without it. */
    oncontext?: (text: string) => void;
    /** Single events drawn faintly under their cohort's bands. */
    traces?: ReportTrace[];
  }

  let { panel, format, heightClass = "h-[280px]", lineColorMode = "continuous", oncontext, traces = [] }: Props = $props();

  type Slot = { aggregate: PercentileAggregate | null; row: ReportRow | null };
  type Point = { slot: number; index: number };

  const xName = $derived(panel.bindings.x ?? "");
  const xKind = $derived(panel.fields.find((f) => f.name === xName)?.kind);
  const median = $derived(boundFields(panel, panel.bindings.y)[0]);
  const edges = $derived(boundFields(panel, panel.bindings.band));
  const seriesName = $derived(panel.bindings.series ?? null);
  const thresholds = $derived(median?.glucoseScale ? (panel.thresholds ?? null) : null);

  const num = (row: ReportRow, name: string | undefined): number | undefined => {
    const value = name ? row[name] : undefined;
    return typeof value === "number" ? value : undefined;
  };

  /** Every x value, ascending; a row's slot is its place here. */
  const xs = $derived(
    [...new Set(panel.rows.map((r) => num(r, xName)).filter((x): x is number => x !== undefined))].toSorted((a, b) => a - b),
  );
  const points = $derived(xs.map((_, index): Point => ({ slot: index + 0.5, index })));

  function aggregateOf(row: ReportRow): PercentileAggregate | null {
    const p50 = num(row, median?.name);
    if (p50 === undefined) return null;
    if (edges.length === 4)
      return {
        p10: num(row, edges[0].name),
        p25: num(row, edges[1].name),
        p50,
        p75: num(row, edges[2].name),
        p90: num(row, edges[3].name),
      };
    return { p25: num(row, edges[0]?.name), p50, p75: num(row, edges[1]?.name) };
  }

  type Group = { key: string; name: string; colorIndex: number; slots: Slot[]; comparison: Slot[] | null };

  const groups = $derived.by(() => {
    const found: Group[] = [];
    const empty = () => xs.map((): Slot => ({ aggregate: null, row: null }));
    for (const row of panel.rows) {
      const x = num(row, xName);
      if (x === undefined) continue;
      const cohort = num(row, "cohort");
      const name = seriesName ? String(row[seriesName] ?? "") : (median?.label ?? "");
      const key = cohort !== undefined ? `cohort-${cohort}` : name;
      let group = found.find((g) => g.key === key);
      if (!group) {
        group = { key, name, colorIndex: cohort !== undefined ? cohort - 1 : found.length, slots: empty(), comparison: null };
        found.push(group);
      }
      if (row.range === "comparison") {
        group.comparison ??= empty();
        group.comparison[xs.indexOf(x)] = { aggregate: aggregateOf(row), row };
      } else {
        group.name = name;
        group.slots[xs.indexOf(x)] = { aggregate: aggregateOf(row), row };
      }
    }
    return found.map((group): Group & { colors: BandColors } => ({
      ...group,
      colors: found.length === 1 && median?.color?.startsWith("glucose") ? GLUCOSE_BANDS : cohortBands(group.colorIndex),
    }));
  });
  const hasComparison = $derived(groups.some((g) => g.comparison));
  /** One glucose series takes the glucose colours; several keep their cohort hues so they read apart. */
  const rangeColoured = $derived(thresholds !== null && groups.length === 1);

  /** A minute from the event on the slot axis: slots are evenly spaced from the first x. */
  const slotStep = $derived(xs.length > 1 ? xs[1] - xs[0] : 1);
  const slotOf = (minute: number) => (minute - xs[0]) / slotStep + 0.5;
  const traceColor = (trace: ReportTrace) => cohortBands(trace.cohort - 1).median;

  const yDomain = $derived.by((): [number, number] => {
    const values = groups.flatMap((g) =>
      g.slots.flatMap((s) => (s.aggregate ? [s.aggregate.p10, s.aggregate.p25, s.aggregate.p50, s.aggregate.p75, s.aggregate.p90] : [])),
    ).concat(traces.flatMap((t) => t.points.map(([, value]) => value)))
      .filter((v): v is number => v !== undefined);
    if (values.length === 0) return [0, 1];
    const min = Math.min(0, ...values);
    const max = Math.max(thresholds?.high ?? 0, ...values);
    const pad = (max - min || 1) * 0.08;
    return [min < 0 ? min - pad : 0, max + pad];
  });

  /** Ticks on the hour (time of day) or every hour from the event, at the slot holding that x. */
  const ticks = $derived(xs.flatMap((x, i) => (x % 60 === 0 ? [i + 0.5] : [])).filter((_, i, all) => all.length <= 12 || i % 2 === 0));
  const xLabel = (x: number) => (xKind === "time_of_day" ? formatMinuteOfDay(x, format) : formatOffset(x, format));
  const tickLabel = (slot: number) => {
    const x = xs[Math.floor(slot)];
    return x === undefined ? "" : xLabel(x);
  };
  const zeroSlot = $derived(xKind === "offset" ? xs.indexOf(0) : -1);
  const pick = (s: Slot) => s.aggregate;

  const unit = $derived(median?.unit ? ` (${median.unit})` : "");
  const withUnit = (value: number | undefined, field: ReportField) =>
    `${formatReportValue(value, field, format)}${field.unit ? ` ${field.unit}` : ""}`;

  /** "P25–P75" from "Glucose P25" and "Glucose P75": the edge labels without the words they share. */
  function spanLabel(low: ReportField, high: ReportField): string {
    const a = low.label.split(" ");
    const b = high.label.split(" ");
    let shared = 0;
    while (shared < a.length - 1 && shared < b.length - 1 && a[shared] === b[shared]) shared++;
    return `${a.slice(shared).join(" ")}–${b.slice(shared).join(" ")}`;
  }
  const spans = $derived(
    edges.length === 4
      ? [
          { label: spanLabel(edges[1], edges[2]), low: "p25", high: "p75" } as const,
          { label: spanLabel(edges[0], edges[3]), low: "p10", high: "p90" } as const,
        ]
      : edges.length === 2
        ? [{ label: spanLabel(edges[0], edges[1]), low: "p25", high: "p75" } as const]
        : [],
  );

  /** The API's sentences for every series at a slot, primary range first. */
  function sentencesAt(index: number): string {
    return groups
      .flatMap((g) => [g.slots[index]?.row, g.comparison?.[index]?.row])
      .map(rowSentence)
      .filter((s): s is string => s !== null)
      .join(" ");
  }

  /** The slot a tooltip datum is, or -1. */
  function slotIndex(datum: unknown): number {
    return datum && typeof datum === "object" && "index" in datum && typeof datum.index === "number" ? datum.index : -1;
  }

  function pickSlot(datum: unknown) {
    const index = slotIndex(datum);
    if (index < 0 || !oncontext) return;
    const text = sentencesAt(index);
    if (text) oncontext(text);
  }
</script>

{#if xs.length === 0 || !median}
  <p class="flex {heightClass} items-center justify-center text-sm text-muted-foreground">
    No data in this range.
  </p>
{:else}
  <div class={[heightClass, "w-full", oncontext && "cursor-pointer"]} data-testid="report-range-bands">
    <Chart
      data={points}
      x={(d: Point) => d.slot}
      xDomain={[0, xs.length]}
      {yDomain}
      padding={{ left: 44, right: 12, top: 8, bottom: 24 }}
      tooltipContext={{ mode: "quadtree-x", onclick: (_e: MouseEvent, { data }: { data: unknown }) => pickSlot(data) }}
    >
      {#snippet children({ context })}
        <Svg>
          <Axis
            placement="left"
            ticks={4}
            grid={{ class: "stroke-border/60" }}
            format={(v: number) => formatReportValue(v, median, format)}
            tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
          />
          {#if thresholds}
            {@const top = context.yScale(thresholds.high)}
            {@const bottom = context.yScale(thresholds.low)}
            <Rect
              x={0}
              y={top}
              width={context.width}
              height={Math.max(0, bottom - top)}
              class="fill-glucose-in-range/10"
              data-testid="report-range-bands-target"
            />
            <Rule y={thresholds.low} class="stroke-glucose-low/60" dashArray="4 4" />
            <Rule y={thresholds.high} class="stroke-glucose-high/60" dashArray="4 4" />
          {/if}
          {#if zeroSlot >= 0}
            <Rule x={zeroSlot} class="stroke-foreground/50" stroke-dasharray="2 2" />
          {/if}
          {#if yDomain[0] < 0}
            <Rule y={0} class="stroke-foreground/40" />
          {/if}
          {#each traces as trace (`${trace.cohort}-${trace.at}`)}
            <Spline
              data={trace.points}
              x={(p: [number, number]) => slotOf(p[0])}
              y={(p: [number, number]) => p[1]}
              stroke={traceColor(trace)}
              stroke-width={1}
              stroke-opacity={0.28}
              fill="none"
              data-testid="report-trace"
            />
          {/each}
          {#each groups as group (group.key)}
            <g data-testid="report-range-bands-series" data-series={group.name} data-color={group.colors.median}>
              {#if rangeColoured && thresholds}
                <LinearGradient
                  stops={reportGlucoseStops(thresholds, lineColorMode, context.yScale, context.height)}
                  units="userSpaceOnUse"
                  vertical
                  y1="0"
                  y2={String(context.height)}
                >
                  {#snippet children({ gradient })}
                    <PercentileBands
                      slots={group.slots}
                      comparison={group.comparison}
                      comparisonColor={group.colors.median}
                      {pick}
                      outerColor={group.colors.outer}
                      innerColor={group.colors.inner}
                      lineColor={gradient}
                      outer={edges.length === 4}
                    />
                  {/snippet}
                </LinearGradient>
              {:else}
                <PercentileBands
                  slots={group.slots}
                  comparison={group.comparison}
                  comparisonColor={group.colors.median}
                  {pick}
                  outerColor={group.colors.outer}
                  innerColor={group.colors.inner}
                  lineColor={group.colors.median}
                  outer={edges.length === 4}
                />
              {/if}
            </g>
          {/each}
          <Axis
            placement="bottom"
            rule={{ class: "stroke-border" }}
            {ticks}
            format={tickLabel}
            tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
          />
        </Svg>

        <Tooltip.Root {context} class="z-50 rounded-lg border border-border text-xs shadow-xl">
          {#snippet children({ data })}
            {@const index = slotIndex(data)}
            <div data-testid="report-range-bands-tooltip">
              <Tooltip.Header value={xs[index]} format={(x: number) => xLabel(x)} class="mb-1 border-b border-border pb-1 font-semibold" />
              <Tooltip.List>
                {#each groups as group (group.key)}
                  {@const aggregate = group.slots[index]?.aggregate}
                  {#if aggregate}
                    <Tooltip.Item label={group.name} value={withUnit(aggregate.p50, median)} color={group.colors.median} />
                    {#each spans as span (span.label)}
                      {#if aggregate[span.low] !== undefined && aggregate[span.high] !== undefined}
                        <Tooltip.Item
                          label={span.label}
                          value={`${formatReportValue(aggregate[span.low], median, format)}–${withUnit(aggregate[span.high], median)}`}
                        />
                      {/if}
                    {/each}
                  {/if}
                {/each}
              </Tooltip.List>
            </div>
          {/snippet}
        </Tooltip.Root>
      {/snippet}
    </Chart>
  </div>
  <ul class="mt-1 flex flex-wrap gap-3 text-xs text-muted-foreground" aria-label="Series">
    {#each groups as group (group.key)}
      <li class="flex items-center gap-1">
        <span class="inline-block size-2.5 rounded-full bg-(--swatch)" style:--swatch={group.colors.median}></span>
        {group.name}
      </li>
    {/each}
    {#if hasComparison}
      <li class="flex items-center gap-1">
        <span class="inline-block w-4 border-t-2 border-dashed border-muted-foreground"></span>
        Comparison range
      </li>
    {/if}
    <li>{median.label}{unit}</li>
  </ul>
{/if}
