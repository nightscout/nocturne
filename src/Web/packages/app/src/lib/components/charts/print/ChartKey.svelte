<!--
  Static legend for a textured chart. Use it in place of layerchart's `legend`,
  whose swatches are plain colour dots that match nothing once the marks print
  as textures. It is not interactive, so it prints as it looks.
-->
<script lang="ts" module>
  import type { TextureKey } from "./chart-print-patterns";

  export interface ChartKeyItem {
    texture: TextureKey;
    label: string;
    color?: string;
    shape?: "fill" | "line" | "dot";
  }
</script>

<script lang="ts">
  import { cn } from "$lib/utils";
  import TextureSwatch from "./TextureSwatch.svelte";

  interface Props {
    items: ChartKeyItem[];
    class?: string;
  }

  let { items, class: className }: Props = $props();
</script>

<ul class={cn("flex flex-wrap items-center justify-center gap-x-4 gap-y-1 text-xs text-muted-foreground", className)}>
  {#each items as item, i (i)}
    <li class="flex items-center gap-1.5">
      <TextureSwatch texture={item.texture} color={item.color} shape={item.shape} />
      <span>{item.label}</span>
    </li>
  {/each}
</ul>
