<script lang="ts">
  import { createConnectionIndicator } from "./connection-indicator.svelte";
  import type { WebSocketConnectionStatus } from "$lib/websocket/types";

  /** Hosts the indicator's `$effect`, which only runs inside a component. */
  let {
    status,
    graceMs,
  }: { status: () => WebSocketConnectionStatus; graceMs: number } = $props();

  // The indicator is built once per mount, as a component does; the test drives
  // it through `status`, not by swapping the props.
  // svelte-ignore state_referenced_locally
  const indicator = createConnectionIndicator(status, graceMs);
</script>

<span data-testid="indicator">
  {indicator.isDisconnected ? "reported" : "quiet"}
</span>
