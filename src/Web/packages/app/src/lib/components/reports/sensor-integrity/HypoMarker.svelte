<svelte:options namespace="svg" />

<!--
  Downward triangle whose tip sits at (x, y). Overnight hypos are filled and
  daytime ones hollow, so the two stay apart without colour.
-->
<script lang="ts">
  interface Props {
    x: number;
    y: number;
    nocturnal: boolean;
  }

  let { x, y, nocturnal }: Props = $props();
</script>

<path
  d={`M ${x - 3.5} ${y - 6} L ${x + 3.5} ${y - 6} L ${x} ${y} Z`}
  stroke-width="1.2"
  stroke-linejoin="round"
  class={nocturnal
    ? "fill-cluster-high stroke-cluster-high"
    : "fill-background stroke-glucose-very-low"}
/>
