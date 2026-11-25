<!--
  StackedBar's static drawing, laid across: the parts at their cumulative positions out of `max`,
  and under the bar a key that prints each part's value and words, as the bar's on-screen key does.
  Drawn at a given place on a static chart's canvas.
-->
<script lang="ts">
  import { Rect, Text } from "layerchart";
  import { paint, staticText, type StaticTheme } from "../../theme/static-theme.js";
  import type { StackedBarSegment } from "../StackedBar.svelte";

  interface Props {
    theme: StaticTheme;
    segments: StackedBarSegment[];
    x: number;
    y: number;
    width: number;
    height: number;
    max?: number;
    formatValue?: (value: number) => string;
  }

  let { theme, segments, x, y, width, height, max = 100, formatValue = (v: number) => `${Math.round(v)}%` }: Props = $props();

  const text = $derived(staticText(theme));
  const total = $derived(Math.max(max, segments.reduce((sum, s) => sum + Math.max(0, s.value), 0)));
  const parts = $derived.by(() => {
    let at = 0;
    return segments.map((segment) => {
      const start = at;
      at += Math.max(0, segment.value);
      return { ...segment, start, end: at };
    });
  });
  /** The key's entries in the bar's order, placed from an estimate of their words' width at 12px, wrapped to the bar's width. */
  const keyItems = $derived.by(() => {
    let left = x;
    let top = y + height + 10;
    return segments.map((segment) => {
      const label = `${formatValue(segment.value)} ${segment.label}`;
      const size = 16 + label.length * 6.4;
      if (left > x && left + size > x + width) {
        left = x;
        top += 18;
      }
      const at = { key: segment.key, label, color: segment.color, x: left, y: top };
      left += size + 16;
      return at;
    });
  });
</script>

{#each parts as part (part.key)}
  {#if part.value > 0}
    <Rect
      x={x + (part.start / total) * width}
      y={y}
      width={((part.end - part.start) / total) * width}
      height={height}
      fill={paint(theme, part.color)}
    />
  {/if}
{/each}
{#each keyItems as item (item.key)}
  <Rect x={item.x} y={item.y} width={10} height={10} fill={paint(theme, item.color)} />
  <Text x={item.x + 16} y={item.y + 10} value={item.label} fill={text.muted} fontSize={12} style={text.font} />
{/each}
