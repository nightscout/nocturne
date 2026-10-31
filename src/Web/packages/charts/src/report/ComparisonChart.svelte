<!--
  Two periods side by side from a period_comparison panel: each period's time in each glucose range
  as one stacked bar (the rows that carry a band colour), then every figure as a comparison row
  under its group. The period names are the `a` and `b` fields' labels, the figures' names, groups,
  units and verdicts are the rows'; the view only formats the numbers.
-->
<script lang="ts">
  import { resolveChartColor } from "../colors.js";
  import { isChartColor } from "../enums.js";
  import type { ChartFormat } from "../format.js";
  import ComparisonRow from "./ComparisonRow.svelte";
  import StackedBar, { type StackedBarSegment } from "./StackedBar.svelte";
  import StaticComparison from "./static/StaticComparison.svelte";
  import type { StaticRender } from "../theme/static-theme.js";
  import { GLUCOSE_PATTERNS } from "./bar-layout.js";
  import { comparisonBar } from "./comparison.js";
  import type { ReportPanel, ReportRow } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    emptyText: string;
    /** Drawn on a canvas in the theme's colours, for an image. */
    static?: StaticRender;
    /** In an image, the host's words for how many figures did not fit. */
    moreRows?: (count: number) => string;
  }

  let { panel, format, emptyText, static: still, moreRows }: Props = $props();

  const label = (name: string) => panel.fields.find((f) => f.name === name)?.label ?? name;
  const text = (row: ReportRow, name: string) => (typeof row[name] === "string" ? String(row[name]) : "");
  const num = (row: ReportRow, name: string) => (typeof row[name] === "number" ? Number(row[name]) : null);

  function withUnit(value: number, unit: string): string {
    const shown = new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(value);
    return unit === "" ? shown : unit === "%" ? `${shown}%` : `${shown} ${unit}`;
  }


  /** One period's time in each range, from the rows whose colour names a glucose range, in their order. */
  function bands(side: "a" | "b"): StackedBarSegment[] {
    return panel.rows.flatMap((row) => {
      const color = row.color;
      const value = num(row, side);
      if (!isChartColor(color) || value === null) return [];
      return [{ key: text(row, "metric"), label: text(row, "label"), value, color: resolveChartColor(color), pattern: GLUCOSE_PATTERNS[color] ?? null }];
    });
  }

  const sides = $derived((["a", "b"] as const).map((side) => ({ side, name: label(side), segments: bands(side) })));

  const groups = $derived.by(() => {
    const found: { name: string; rows: ReportRow[] }[] = [];
    for (const row of panel.rows) {
      const name = text(row, "group");
      let group = found.find((g) => g.name === name);
      if (!group) found.push((group = { name, rows: [] }));
      group.rows.push(row);
    }
    return found;
  });

  function rowProps(row: ReportRow) {
    const unit = text(row, "unit");
    const a = num(row, "a");
    const b = num(row, "b");
    const delta = num(row, "delta");
    const change = num(row, "changePercent");
    const verdict: "better" | "worse" | null = row.verdict === "better" ? "better" : row.verdict === "worse" ? "worse" : null;
    const deltaText =
      delta === null ? "–" : delta === 0 ? `±${withUnit(0, unit)}` : `${delta > 0 ? "+" : "−"}${withUnit(Math.abs(delta), unit)}`;
    return {
      label: text(row, "label"),
      aText: a === null ? emptyText : withUnit(a, unit),
      bText: b === null ? emptyText : withUnit(b, unit),
      deltaText,
      verdict,
      ...comparisonBar(change ?? 0, row.direction !== "up" && row.direction !== "down"),
    };
  }
</script>

{#if still}
  <StaticComparison
    {still}
    title={panel.title ?? panel.dataset}
    context={panel.context}
    {sides}
    {groups}
    rowOf={rowProps}
    columns={{ label: label("label"), a: label("a"), b: label("b"), delta: label("delta") }}
    {moreRows}
  />
{:else if panel.rows.length === 0}
  <p class="py-6 text-center text-sm text-muted-foreground">{emptyText}</p>
{:else}
  <div class="@container flex flex-col gap-4" data-testid="report-comparison">
    <div class="grid gap-4 @xl:grid-cols-2">
      {#each sides as side (side.side)}
        <div class="flex flex-col gap-2" data-testid="comparison-side" data-side={side.side}>
          <p class="text-sm font-semibold">{side.name}</p>
          {#if side.segments.length > 0}
            <StackedBar segments={side.segments} orientation="horizontal" keyOnScreen compact />
          {:else}
            <p class="text-xs text-muted-foreground">{emptyText}</p>
          {/if}
        </div>
      {/each}
    </div>

    <div class="divide-y divide-border">
      <div class="hidden gap-4 px-3 pb-1 text-xs font-medium text-muted-foreground @2xl:grid @2xl:[grid-template-columns:minmax(140px,1fr)_90px_90px_minmax(120px,2fr)_100px]">
        <div>{label("label")}</div>
        <div class="text-right">{label("a")}</div>
        <div class="text-right">{label("b")}</div>
        <div></div>
        <div class="text-right">{label("delta")}</div>
      </div>
      {#each groups as group (group.name)}
        <div role="heading" aria-level={4} class="px-3 pt-4 pb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {group.name}
        </div>
        {#each group.rows as row (text(row, "metric"))}
          <ComparisonRow {...rowProps(row)} />
        {/each}
      {/each}
    </div>
  </div>
{/if}
