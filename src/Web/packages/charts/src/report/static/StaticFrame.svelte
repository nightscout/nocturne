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

  /**
   * The room the frame takes around the plot: above it for the title, below it for the axis and the key.
   * `key` is whether there is a key, or how many rows it wraps to (staticKeyRows).
   */
  export function staticPadding(options: { key: boolean | number; left?: number; right?: number; bottom?: number }) {
    const rows = typeof options.key === "number" ? options.key : options.key ? 1 : 0;
    return {
      top: STATIC_HEADER,
      left: options.left ?? 48,
      right: options.right ?? 20,
      bottom: (options.bottom ?? 28) + (rows > 0 ? STATIC_KEY + STATIC_KEY_ROW * (rows - 1) : 0),
    };
  }

  export const STATIC_HEADER = 58;
  export const STATIC_KEY = 26;
  /** The height each further row of a wrapped key adds. */
  export const STATIC_KEY_ROW = 18;
  /** The image's margin each side of the key. */
  const KEY_MARGIN = 16;

  /** Each key entry's place: rows of entries from an estimate of their words' width at 12px, wrapped at `width`. */
  export function placeStaticKey(key: readonly StaticKeyItem[], width: number): (StaticKeyItem & { x: number; textX: number; row: number })[] {
    let x = 0;
    let row = 0;
    return key.map((item) => {
      const swatch = item.color === null ? 0 : 20;
      const size = swatch + item.label.length * 6.4 + 20;
      if (x > 0 && x + size > width - 2 * KEY_MARGIN) {
        x = 0;
        row++;
      }
      const at = x;
      x += size;
      return { ...item, x: at, textX: at + swatch, row };
    });
  }

  /** How many rows a key wraps to on an image `width` pixels wide; 0 for no key. */
  export function staticKeyRows(key: readonly StaticKeyItem[], width: number): number {
    return key.length === 0 ? 0 : Math.max(...placeStaticKey(key, width).map((p) => p.row)) + 1;
  }
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
  const width = $derived(chart.width + chart.padding.left + chart.padding.right);
  const placed = $derived(
    placeStaticKey(key, width).map((p) => ({ ...p, x: left + p.x, textX: left + p.textX })),
  );
  const rows = $derived(placed.length === 0 ? 0 : Math.max(...placed.map((p) => p.row)) + 1);
  /** The baseline of each key row: the last row where a single row always sat, the others above it. */
  const keyY = (row: number) => chart.height + chart.padding.bottom - 12 - STATIC_KEY_ROW * (rows - 1 - row);
</script>

{#if title}
  <Text x={left} y={top + 24} value={title} fill={text.foreground} fontSize={15} style={text.strong} />
{/if}
{#if context}
  <!-- An untitled chart's range line takes the title's place. -->
  <Text x={left} y={top + (title ? 44 : 24)} value={context} fill={text.muted} fontSize={12} style={text.font} />
{/if}

{#each placed as item, i (i)}
  {@const y = keyY(item.row)}
  {#if item.color === null}
    <!-- Named only: a line coloured by glucose has no one colour to show. -->
  {:else if item.shape === "line" || item.shape === "dashed"}
    <Line
      x1={item.x}
      x2={item.x + 14}
      y1={y - 4}
      y2={y - 4}
      stroke={item.color}
      strokeWidth={2}
      dashArray={item.shape === "dashed" ? "4 3" : undefined}
    />
  {:else}
    <Rect x={item.x} y={y - 10} width={10} height={10} fill={item.color} />
  {/if}
  <Text x={item.textX} y={y} value={item.label} fill={text.muted} fontSize={12} style={text.font} />
{/each}
