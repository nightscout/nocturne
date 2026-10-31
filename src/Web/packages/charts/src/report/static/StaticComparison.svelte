<!--
  ComparisonChart's static mode: each period's time in each range as a stacked bar with its key,
  then the figures as rows (name, A, B, the change bar, the change), painted on a canvas. Rows that
  do not fit the image are counted in a last line; the summary carries every figure.
-->
<script lang="ts">
  import { Canvas, Chart, Line, Rect, Text } from "layerchart";
  import { paint, staticText, type StaticRender } from "../../theme/static-theme.js";
  import type { ComparisonBar } from "../comparison.js";
  import type { StackedBarSegment } from "../StackedBar.svelte";
  import type { ReportRow } from "../types.js";
  import StaticFrame, { staticPadding } from "./StaticFrame.svelte";
  import StaticStackedBar from "./StaticStackedBar.svelte";

  type Row = ComparisonBar & { label: string; aText: string; bText: string; deltaText: string; verdict: "better" | "worse" | null };

  interface Props {
    still: StaticRender;
    title: string;
    context?: string | null;
    sides: { side: string; name: string; segments: StackedBarSegment[] }[];
    groups: { name: string; rows: ReportRow[] }[];
    rowOf: (row: ReportRow) => Row;
    columns: { label: string; a: string; b: string; delta: string };
    /** The host's words for the last line when rows are left out: how many the image does not show. No line without it. */
    moreRows?: (count: number) => string;
  }

  let {
    still,
    title,
    context = null,
    sides,
    groups,
    rowOf,
    columns,
    moreRows,
  }: Props = $props();

  const theme = $derived(still.theme);
  const text = $derived(staticText(theme));
  const padding = staticPadding({ key: false, left: 16, right: 16, bottom: 12 });
  const ROW = 24;
  const HEADING = 26;
  const TOP = 92;

  /** Each line under the bars at its y: group headings and rows, until the image is full. */
  function lines(height: number) {
    const placed: ({ kind: "heading"; name: string; y: number } | { kind: "row"; row: Row; y: number })[] = [];
    let y = TOP + 22;
    let left = 0;
    for (const group of groups) {
      for (const [i, row] of group.rows.entries()) {
        const needs = (i === 0 ? HEADING : 0) + ROW;
        if (left > 0 || y + needs > height - ROW) {
          left++;
          continue;
        }
        if (i === 0) {
          placed.push({ kind: "heading", name: group.name, y: y + 14 });
          y += HEADING;
        }
        placed.push({ kind: "row", row: rowOf(row), y: y + 16 });
        y += ROW;
      }
    }
    return { placed, left, end: y };
  }

  const verdictColor = (verdict: Row["verdict"]) =>
    paint(theme, verdict === "better" ? "var(--status-normal)" : verdict === "worse" ? "var(--status-critical)" : "var(--foreground)");
</script>

<Chart ssr width={still.width} height={still.height} data={[]} {padding}>
  {#snippet children({ context: chart })}
    {@const W = chart.width}
    {@const half = (W - 24) / 2}
    {@const layout = lines(chart.height)}
    {@const aRight = W * 0.46}
    {@const bRight = W * 0.58}
    {@const barLeft = W * 0.62}
    {@const barRight = W * 0.86}
    <Canvas ssrCapture={still.capture}>
      <StaticFrame {theme} {title} {context} />
      {#each sides as side, i (side.side)}
        {@const x = i * (half + 24)}
        <Text x={x} y={12} value={side.name} fill={text.foreground} fontSize={13} style={text.strong} />
        <StaticStackedBar {theme} segments={side.segments} x={x} y={22} width={half} height={16} />
      {/each}

      <Text x={0} y={TOP + 12} value={columns.label} fill={text.muted} fontSize={11} style={text.strong} />
      <Text x={aRight} y={TOP + 12} value={columns.a} textAnchor="end" fill={text.muted} fontSize={11} style={text.strong} />
      <Text x={bRight} y={TOP + 12} value={columns.b} textAnchor="end" fill={text.muted} fontSize={11} style={text.strong} />
      <Text x={W} y={TOP + 12} value={columns.delta} textAnchor="end" fill={text.muted} fontSize={11} style={text.strong} />
      <Line x1={0} x2={W} y1={TOP + 20} y2={TOP + 20} stroke={text.border} />

      {#each layout.placed as line, i (i)}
        {#if line.kind === "heading"}
          <Text x={0} y={line.y} value={line.name.toUpperCase()} fill={text.muted} fontSize={11} style={text.strong} />
        {:else}
          {@const row = line.row}
          {@const mid = (barLeft + barRight) / 2}
          {@const reach = ((barRight - barLeft) * row.barWidth) / 100}
          <Text x={0} y={line.y} value={row.label} fill={text.foreground} fontSize={13} style={text.font} />
          <Text x={aRight} y={line.y} value={row.aText} textAnchor="end" fill={text.muted} fontSize={13} style={text.font} />
          <Text x={bRight} y={line.y} value={row.bText} textAnchor="end" fill={text.foreground} fontSize={13} style={text.strong} />
          <Rect x={barLeft} y={line.y - 8} width={barRight - barLeft} height={8} rx={4} fill={paint(theme, "var(--muted)")} />
          <Line x1={mid} x2={mid} y1={line.y - 8} y2={line.y} stroke={text.border} />
          {#if row.bar === "up"}
            <Rect x={mid} y={line.y - 8} width={reach} height={8} rx={4} fill={verdictColor(row.verdict)} />
          {:else if row.bar === "down"}
            <Rect x={mid - reach} y={line.y - 8} width={reach} height={8} rx={4} fill={verdictColor(row.verdict)} />
          {:else}
            <Rect x={mid - 1} y={line.y - 8} width={2} height={8} fill={verdictColor(row.verdict)} />
          {/if}
          <Text x={W} y={line.y} value={row.deltaText} textAnchor="end" fill={verdictColor(row.verdict)} fontSize={13} style={text.strong} />
        {/if}
      {/each}
      {#if layout.left > 0 && moreRows}
        <Text x={0} y={chart.height - 4} value={moreRows(layout.left)} fill={text.muted} fontSize={12} style={text.font} />
      {/if}
    </Canvas>
  {/snippet}
</Chart>
