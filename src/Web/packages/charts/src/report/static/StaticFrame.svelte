<!--
  The words around a static chart, drawn on its canvas: the panel's title and range line above the
  plot, and a key under it. Sits inside the chart, so it reads the plot's padding from the context.
-->
<script lang="ts" module>
  /** One entry of a static chart's key: a swatch in its colour (a dash for a line) and its words; no swatch without a colour. */
  export interface StaticKeyItem {
    label: string;
    color: string | null;
    shape?: "square" | "line" | "dashed";
  }

  /** The room the frame takes around the plot: above it for the title, below it for the axis and the key. */
  export function staticPadding(options: { key: boolean; left?: number; right?: number; bottom?: number }) {
    return {
      top: STATIC_HEADER,
      left: options.left ?? 48,
      right: options.right ?? 20,
      bottom: (options.bottom ?? 28) + (options.key ? STATIC_KEY : 0),
    };
  }

  export const STATIC_HEADER = 58;
  export const STATIC_KEY = 26;
</script>

<script lang="ts">
  import { getChartContext, Line, Rect, Text } from "layerchart";
  import { staticText, type StaticTheme } from "../../theme/static-theme.js";

  interface Props {
    theme: StaticTheme;
    title: string;
    context?: string | null;
    key?: StaticKeyItem[];
  }

  let { theme, title, context = null, key = [] }: Props = $props();

  const chart = getChartContext();
  const text = $derived(staticText(theme));
  const left = $derived(-chart.padding.left + 16);
  const top = $derived(-chart.padding.top);
  const keyY = $derived(chart.height + chart.padding.bottom - 12);

  /** Each key entry's x, from an estimate of its words' width at 12px. */
  const placed = $derived.by(() => {
    let x = left;
    return key.map((item) => {
      const at = x;
      const swatch = item.color === null ? 0 : 20;
      x += swatch + item.label.length * 6.4 + 20;
      return { ...item, x: at, textX: at + swatch };
    });
  });
</script>

<Text x={left} y={top + 24} value={title} fill={text.foreground} fontSize={15} style={text.strong} />
{#if context}
  <Text x={left} y={top + 44} value={context} fill={text.muted} fontSize={12} style={text.font} />
{/if}

{#each placed as item (item.label)}
  {#if item.color === null}
    <!-- Named only: a line coloured by glucose has no one colour to show. -->
  {:else if item.shape === "line" || item.shape === "dashed"}
    <Line
      x1={item.x}
      x2={item.x + 14}
      y1={keyY - 4}
      y2={keyY - 4}
      stroke={item.color}
      strokeWidth={2}
      dashArray={item.shape === "dashed" ? "4 3" : undefined}
    />
  {:else}
    <Rect x={item.x} y={keyY - 10} width={10} height={10} fill={item.color} />
  {/if}
  <Text x={item.textX} y={keyY} value={item.label} fill={text.muted} fontSize={12} style={text.font} />
{/each}
