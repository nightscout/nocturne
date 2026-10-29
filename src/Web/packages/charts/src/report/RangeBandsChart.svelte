<!--
  Percentile bands under a median line from a report panel's rows, one set per value of the series
  field, over minutes from an event or the time of day. Rows are laid out in slots by their x
  value, so every series shares one axis; a missing value is a gap. Each series takes a cohort
  colour, in order, as the Meals & events report colours its cohorts.
-->
<script lang="ts">
  import { Axis, Chart, Rule, Svg } from "layerchart";
  import PercentileBands from "./PercentileBands.svelte";
  import { cohortBands, GLUCOSE_BANDS, type BandColors, type PercentileAggregate } from "./percentile-bands.js";
  import {
    boundFields,
    formatMinuteOfDay,
    formatOffset,
    formatReportValue,
    type ReportPanel,
    type ReportRow,
  } from "./types.js";
  import type { ChartFormat } from "../format.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
  }

  let { panel, format, heightClass = "h-[280px]" }: Props = $props();

  type Slot = { aggregate: PercentileAggregate | null };

  const xName = $derived(panel.bindings.x ?? "");
  const xKind = $derived(panel.fields.find((f) => f.name === xName)?.kind);
  const median = $derived(boundFields(panel, panel.bindings.y)[0]);
  const edges = $derived(boundFields(panel, panel.bindings.band));
  const seriesName = $derived(panel.bindings.series ?? null);

  const num = (row: ReportRow, name: string | undefined): number | undefined => {
    const value = name ? row[name] : undefined;
    return typeof value === "number" ? value : undefined;
  };

  /** Every x value, ascending; a row's slot is its place here. */
  const xs = $derived(
    [...new Set(panel.rows.map((r) => num(r, xName)).filter((x): x is number => x !== undefined))].toSorted((a, b) => a - b),
  );

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

  const groups = $derived.by(() => {
    const found: { name: string; slots: Slot[] }[] = [];
    for (const row of panel.rows) {
      const x = num(row, xName);
      if (x === undefined) continue;
      const name = seriesName ? String(row[seriesName] ?? "") : (median?.label ?? "");
      let group = found.find((g) => g.name === name);
      if (!group) {
        group = { name, slots: xs.map(() => ({ aggregate: null })) };
        found.push(group);
      }
      group.slots[xs.indexOf(x)] = { aggregate: aggregateOf(row) };
    }
    return found.map((group, i): { name: string; slots: Slot[]; colors: BandColors } => ({
      ...group,
      colors: found.length === 1 && median?.color?.startsWith("glucose") ? GLUCOSE_BANDS : cohortBands(i),
    }));
  });

  const yDomain = $derived.by((): [number, number] => {
    const values = groups.flatMap((g) =>
      g.slots.flatMap((s) => (s.aggregate ? [s.aggregate.p10, s.aggregate.p25, s.aggregate.p50, s.aggregate.p75, s.aggregate.p90] : [])),
    ).filter((v): v is number => v !== undefined);
    if (values.length === 0) return [0, 1];
    const min = Math.min(0, ...values);
    const max = Math.max(...values);
    const pad = (max - min || 1) * 0.08;
    return [min < 0 ? min - pad : 0, max + pad];
  });

  /** Ticks on the hour (time of day) or every hour from the event, at the slot holding that x. */
  const ticks = $derived(xs.flatMap((x, i) => (x % 60 === 0 ? [i + 0.5] : [])).filter((_, i, all) => all.length <= 12 || i % 2 === 0));
  const tickLabel = (slot: number) => {
    const x = xs[Math.floor(slot)];
    if (x === undefined) return "";
    return xKind === "time_of_day" ? formatMinuteOfDay(x, format) : formatOffset(x, format);
  };
  const zeroSlot = $derived(xKind === "offset" ? xs.indexOf(0) : -1);
  const pick = (s: Slot) => s.aggregate;

  const unit = $derived(median?.unit ? ` (${median.unit})` : "");
</script>

{#if xs.length === 0 || !median}
  <p class="flex {heightClass} items-center justify-center text-sm text-muted-foreground">
    No data in this range.
  </p>
{:else}
  <div class="{heightClass} w-full" data-testid="report-range-bands">
    <Chart data={[]} xDomain={[0, xs.length]} {yDomain} padding={{ left: 44, right: 12, top: 8, bottom: 24 }}>
      <Svg>
        <Axis
          placement="left"
          ticks={4}
          grid={{ class: "stroke-border/60" }}
          format={(v: number) => formatReportValue(v, median, format)}
          tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
        />
        {#if zeroSlot >= 0}
          <Rule x={zeroSlot} class="stroke-foreground/50" stroke-dasharray="2 2" />
        {/if}
        {#if yDomain[0] < 0}
          <Rule y={0} class="stroke-foreground/40" />
        {/if}
        {#each groups as group (group.name)}
          <g data-testid="report-range-bands-series" data-series={group.name}>
            <PercentileBands
              slots={group.slots}
              {pick}
              outerColor={group.colors.outer}
              innerColor={group.colors.inner}
              lineColor={group.colors.median}
              outer={edges.length === 4}
            />
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
    </Chart>
  </div>
  <ul class="mt-1 flex flex-wrap gap-3 text-xs text-muted-foreground" aria-label="Series">
    {#each groups as group (group.name)}
      <li class="flex items-center gap-1">
        <span class="inline-block size-2.5 rounded-full bg-(--swatch)" style:--swatch={group.colors.median}></span>
        {group.name}
      </li>
    {/each}
    <li>{median.label}{unit}</li>
  </ul>
{/if}
