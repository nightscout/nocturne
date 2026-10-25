<script lang="ts">
  import type { Component } from "svelte";
  import * as Tooltip from "$lib/components/ui/tooltip";

  // The app layout supplies the tooltip provider; this stands in for it around the component under test.
  // The component's props type is unknown here, so any is the only type its props satisfy.
  let {
    component: Inner,
    componentProps = {},
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  }: { component: Component<any>; componentProps?: Record<string, unknown> } = $props();
</script>

<Tooltip.Provider>
  <Inner {...componentProps} />
</Tooltip.Provider>
