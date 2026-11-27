<script lang="ts">
  import type { Fallback } from "./people-tab.js";

  const { fallback }: { fallback: Fallback } = $props();
</script>

{#if fallback.kind === "view" && fallback.reason === "unknown"}
  You have no view with that link. The list shows everyone you follow.
{:else if fallback.kind === "view"}
  Your views did not load. The list shows everyone you follow.
{:else if fallback.reason === "unknown"}
  You are not in that group. The list shows everyone you follow.
{:else}
  Your groups did not load. The list shows everyone you follow.
{/if}
