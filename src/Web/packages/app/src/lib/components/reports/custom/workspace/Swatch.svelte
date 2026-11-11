<!--
  A 10px round mark in the colour the chart drew something in: a glucose line coloured by range shows
  the range colours, and something drawn in more than one colour is a hollow ring. A series the chart
  hatches is a small square with the same hatch. It is never the only channel: a label or a letter is
  always beside it.
-->
<script lang="ts">
  import { textureBackground } from "@nocturne/charts/core";
  import type { Swatch } from "./result-colors.js";

  interface Props {
    swatch: Swatch;
  }

  let { swatch }: Props = $props();

  const texture = $derived(swatch.kind === "solid" ? (swatch.texture ?? null) : null);
  const hatch = $derived(texture ? textureBackground(texture) : null);
</script>

<span
  class={[
    "inline-block shrink-0",
    texture ? "size-3 rounded-xs bg-(image:--hatch) bg-size-(--hatch-size)" : "size-2.5 rounded-full",
    swatch.kind === "solid" && "bg-(--swatch)",
    swatch.kind === "glucose" &&
      "bg-linear-to-br from-glucose-low via-glucose-in-range to-glucose-high",
    swatch.kind === "none" && "border border-muted-foreground",
  ]}
  style:--hatch={hatch?.image}
  style:--hatch-size={hatch?.size}
  style:--swatch={swatch.kind === "solid" ? swatch.color : undefined}
  data-texture={texture ?? undefined}
  aria-hidden="true"
></span>
