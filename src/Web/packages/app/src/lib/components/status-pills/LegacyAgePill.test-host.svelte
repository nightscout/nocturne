<script lang="ts">
  import type { LegacyFallbackDto } from "$lib/api/generated/nocturne-api-client";
  import { hiddenLegacyFallbacks } from "$lib/stores/appearance-store.svelte";
  import LegacyAgePill from "./LegacyAgePill.svelte";
  import { visibleLegacyFallbacks } from "./tracker-pill-visibility";

  let { fallbacks, now }: { fallbacks: LegacyFallbackDto[]; now: number } = $props();

  // Stands in for StatusRail: the pills it shows and the container it marks as their rail.
  const visible = $derived(visibleLegacyFallbacks(fallbacks, hiddenLegacyFallbacks.current));
</script>

<div data-pill-rail role="group" aria-label="Status pills" data-testid="rail">
  {#each visible as fallback (fallback.kind)}
    <LegacyAgePill {fallback} {now} />
  {/each}
</div>
