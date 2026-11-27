<script lang="ts">
  import type { FallbackReason } from "./people-tab.js";

  const { reason }: { reason: FallbackReason } = $props();
</script>

{#if reason === "unknown"}
  You are not in that group. The list shows everyone you follow.
{:else}
  Your groups did not load. The list shows everyone you follow.
{/if}
