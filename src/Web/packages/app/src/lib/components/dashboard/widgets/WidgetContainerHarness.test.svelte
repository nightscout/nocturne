<script lang="ts">
  // Stands in for WidgetGrid's @container: wide enough for its three-column row, or a phone's.
  import type { Component } from "svelte";

  // The widget's props type is unknown here, so any is the only type its props satisfy.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  let { component: Widget, wide }: { component: Component<any>; wide: boolean } = $props();
</script>

<div class={wide ? "@container w-[900px]" : "@container w-[360px]"}>
  <Widget />
</div>
