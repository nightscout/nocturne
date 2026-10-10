<script module lang="ts">
  /**
   * `initializeCalls` is counted by the test's spy on the realtime store; `seenAtInit` is what
   * it was when this child, standing in for the page, initialised.
   */
  export const probe = { initializeCalls: 0, seenAtInit: -1 };
</script>

<script lang="ts">
  import type { Snippet } from "svelte";

  const { children }: { children: Snippet } = $props();
  probe.seenAtInit = probe.initializeCalls;
</script>

{@render children()}
