<script module lang="ts">
  export const navigationProbe = { refreshes: 0 };
</script>

<script lang="ts">
  import type { Snippet } from "svelte";
  import { refreshOnReturn } from "#lib/utils/refresh-on-return.svelte.js";

  const { children }: { children: Snippet } = $props();
  refreshOnReturn(() => navigationProbe.refreshes++);
</script>

{@render children()}
