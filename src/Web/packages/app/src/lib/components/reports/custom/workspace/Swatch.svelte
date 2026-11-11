<!--
  A 10px round mark in the colour the chart drew something in: a glucose line coloured by range shows
  the range colours, and something drawn in more than one colour is a hollow ring. It is never the only
  channel: a label or a letter is always beside it.
-->
<script lang="ts">
  import type { Swatch } from "./result-colors.js";

  interface Props {
    swatch: Swatch;
  }

  let { swatch }: Props = $props();
</script>

<span
  class={[
    "inline-block size-2.5 shrink-0 rounded-full",
    swatch.kind === "solid" && "bg-(--swatch)",
    swatch.kind === "glucose" &&
      "bg-linear-to-br from-glucose-low via-glucose-in-range to-glucose-high",
    swatch.kind === "none" && "border border-muted-foreground",
  ]}
  style:--swatch={swatch.kind === "solid" ? swatch.color : undefined}
  aria-hidden="true"
></span>
