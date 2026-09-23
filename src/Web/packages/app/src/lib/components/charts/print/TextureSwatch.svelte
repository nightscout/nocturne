<!--
  A legend swatch wearing the same colour and texture (or dash) as the chart
  mark it keys, so a black-and-white page can still be read against its legend.
-->
<script lang="ts">
  import { cn } from "$lib/utils";
  import { CHART_TEXTURES, dashClass, patternClass, type TextureKey } from "./chart-print-patterns";

  interface Props {
    texture: TextureKey;
    /** Overrides the key's own colour; required for categorical (`cat-N`) keys. */
    color?: string;
    /** `line` keys a stroked series; `fill` an area, bar or band; `dot` a scatter of points. */
    shape?: "fill" | "line" | "dot";
    class?: string;
  }

  let { texture, color, shape = "fill", class: className }: Props = $props();

  const spec = $derived(CHART_TEXTURES[texture]);
  const paint = $derived(color ?? ("color" in spec ? spec.color : undefined) ?? "currentColor");
</script>

<!-- A line swatch is wide enough to show two cycles of the longest dash. -->
<svg
  viewBox={shape === "line" ? "0 0 44 12" : "0 0 16 12"}
  aria-hidden="true"
  class={cn("inline-block h-3 shrink-0", shape === "line" ? "w-11" : "w-4", className)}
>
  {#if shape === "line"}
    <line x1="0" y1="6" x2="44" y2="6" stroke={paint} stroke-width="2.5" class={dashClass(texture)} />
  {:else if shape === "dot"}
    <circle cx="8" cy="6" r="3" fill={paint} />
  {:else}
    <rect x="0.5" y="0.5" width="15" height="11" rx="2" fill={paint} stroke="var(--border)" class={patternClass(texture)} />
  {/if}
</svg>
